using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

public class CrossListingExporterTests
{
    private static CrossListingExporter NewExporter() => new(new CrossListingFeeProfile(), new FeeProfile());

    private static CrossListRequest SampleDraft() => new()
    {
        Title = "Bitmain Antminer S19j Pro 104TH Bitcoin Miner",
        Description = "<p>Tested and working.</p><ul><li>Hashrate 104TH</li></ul>",
        Condition = "USED_VERY_GOOD",
        Brand = "Bitmain",
        Mpn = "S19J-PRO-104",
        Upc = "012345678905",
        Price = 100m,
        Quantity = 1,
        WeightLbs = 2m,
        WeightOz = 8m,
        ItemLocationPostalCode = "89101",
        ImageUrls = ["https://cdn.example.com/a.jpg", "https://cdn.example.com/b.jpg"],
        ItemSpecifics = new Dictionary<string, string> { ["Hashrate"] = "104 TH/s", ["Type"] = "ASIC Miner" },
    };

    private static CrossListingResult For(CrossListingExport export, string marketplace) =>
        export.Listings.Single(l => l.Marketplace == marketplace);

    [Fact]
    public void Export_NoTargets_ReturnsAllThreeMarketplaces()
    {
        var export = NewExporter().Export(SampleDraft());

        Assert.Equal(3, export.Listings.Count);
        Assert.Equal(CrossListingExporter.AllMarketplaces.OrderBy(m => m),
                     export.Listings.Select(l => l.Marketplace).OrderBy(m => m));
    }

    [Fact]
    public void Export_UnknownTargetName_IsIgnoredRatherThanThrowing()
    {
        var req = SampleDraft();
        req.Targets = ["mercari", "etsy"];

        var export = NewExporter().Export(req);

        Assert.Single(export.Listings);
        Assert.Equal("mercari", export.Listings[0].Marketplace);
    }

    // ── Net-parity pricing — the whole point of the feature ───────────────────

    [Fact]
    public void NetParityPrice_OnAmazon_IsHigherAndLeavesTheSameTakeHomeAsEbay()
    {
        var export = NewExporter().Export(SampleDraft());
        var amazon = For(export, "amazon");

        // eBay: $100 - (13.25% + $0.40) = $86.35 net. Amazon's 15% referral fee needs a higher
        // sticker price to leave the same $86.35 behind.
        Assert.Equal(86.35m, export.EbayNet);
        Assert.True(amazon.NetParityPrice > export.EbayPrice);

        var netAtParity = amazon.NetParityPrice - amazon.NetParityPrice * 0.15m;
        Assert.InRange(netAtParity, export.EbayNet, export.EbayNet + 0.02m);
    }

    [Fact]
    public void NetParityPrice_OnAZeroFeeMarketplace_IsBelowTheEbayPrice()
    {
        // Mercari charges the seller nothing, so the seller can undercut their own eBay listing
        // and still take home the same money. That headroom is the number worth surfacing.
        var export = NewExporter().Export(SampleDraft());
        var mercari = For(export, "mercari");

        Assert.Equal(0m, mercari.EstimatedFees);
        Assert.Equal(export.EbayNet, mercari.NetParityPrice);
        Assert.True(mercari.NetParityPrice < export.EbayPrice);
    }

    [Fact]
    public void NetParityPrice_RoundsUpSoRoundingNeverCostsTheSeller()
    {
        var export = NewExporter().Export(SampleDraft());
        var facebook = For(export, "facebook");

        Assert.Equal(facebook.NetParityPrice, Math.Round(facebook.NetParityPrice, 2));
        Assert.True(facebook.NetParityPrice - facebook.NetParityPrice * 0.05m >= export.EbayNet);
    }

    [Fact]
    public void Export_ZeroPrice_ProducesZeroPricingAndAWarningInsteadOfNonsense()
    {
        var req = SampleDraft();
        req.Price = 0m;

        var export = NewExporter().Export(req);

        Assert.Equal(0m, export.EbayNet);
        Assert.Contains(export.Warnings, w => w.Contains("no price", StringComparison.OrdinalIgnoreCase));
        Assert.All(export.Listings, l => Assert.Equal(0m, l.NetParityPrice));
    }

    // ── Titles ────────────────────────────────────────────────────────────────

    [Fact]
    public void Title_ForMercari_IsCutToEightyCharsOnAWordBoundary()
    {
        var req = SampleDraft();
        req.Title = "Bitmain Antminer S19j Pro 104TH Bitcoin Miner ASIC with PSU Power Supply Included Ready to Mine";

        var mercari = For(NewExporter().Export(req), "mercari");

        Assert.True(mercari.TitleTruncated);
        Assert.True(mercari.Title.Length <= 80);
        Assert.DoesNotContain("  ", mercari.Title);
        // Word boundary, not a mid-word chop.
        Assert.StartsWith(mercari.Title, req.Title, StringComparison.Ordinal);
        Assert.Contains(mercari.Warnings, w => w.Contains("80-character"));
    }

    [Fact]
    public void Title_StripsCrossSiteReferencesAndShippingNoise()
    {
        var req = SampleDraft();
        req.Title = "eBay Exclusive! Antminer S19j Pro - FREE SHIPPING - L@@K";

        var facebook = For(NewExporter().Export(req), "facebook");

        Assert.DoesNotContain("ebay", facebook.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FREE SHIPPING", facebook.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("L@@K", facebook.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Antminer S19j Pro", facebook.Title);
        Assert.False(facebook.Title.EndsWith('-'));
    }

    [Fact]
    public void Title_WithinLimit_IsNotFlaggedAsTruncated()
    {
        var amazon = For(NewExporter().Export(SampleDraft()), "amazon");

        Assert.False(amazon.TitleTruncated);
        Assert.Equal(200, amazon.TitleLimit);
    }

    // ── Descriptions ──────────────────────────────────────────────────────────

    [Fact]
    public void HtmlToText_ConvertsMarkupToPlainTextWithBullets()
    {
        var text = CrossListingExporter.HtmlToText("<p>Great item.</p><ul><li>Fast</li><li>Quiet</li></ul>");

        Assert.DoesNotContain("<", text);
        Assert.Contains("Great item.", text);
        Assert.Contains("• Fast", text);
        Assert.Contains("• Quiet", text);
    }

    [Fact]
    public void HtmlToText_DecodesEntities()
    {
        Assert.Equal("Tom & Jerry's \"best\"",
            CrossListingExporter.HtmlToText("Tom &amp; Jerry&#39;s &quot;best&quot;"));
    }

    [Fact]
    public void Description_DropsLinesMentioningEbayAndSaysSo()
    {
        var req = SampleDraft();
        req.Description = "Tested and working.\nSee my other eBay listings!\nShips same day.";

        var mercari = For(NewExporter().Export(req), "mercari");

        Assert.DoesNotContain("eBay", mercari.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tested and working.", mercari.Description);
        Assert.Contains("Ships same day.", mercari.Description);
        Assert.Contains(mercari.Warnings, w => w.Contains("mentioned eBay"));
    }

    [Fact]
    public void Description_CarriesItemSpecificsIntoProseForSitesWithoutSpecificsFields()
    {
        // Facebook and Mercari have no Item Specifics equivalent — without this the structured
        // data the seller entered on eBay just vanishes.
        var facebook = For(NewExporter().Export(SampleDraft()), "facebook");

        Assert.Contains("Hashrate: 104 TH/s", facebook.Description);
        Assert.Contains("Brand: Bitmain", facebook.Description);
    }

    [Fact]
    public void Description_ForMercari_IsCutToOneThousandChars()
    {
        var req = SampleDraft();
        req.Description = string.Join(" ", Enumerable.Repeat("Detailed condition notes here.", 120));

        var mercari = For(NewExporter().Export(req), "mercari");

        Assert.True(mercari.DescriptionTruncated);
        Assert.True(mercari.Description.Length <= 1000);
    }

    // ── Condition mapping ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("NEW", "New", "New", "New")]
    [InlineData("LIKE_NEW", "Used - Like New", "Like new", "UsedLikeNew")]
    [InlineData("USED_VERY_GOOD", "Used - Good", "Good", "UsedVeryGood")]
    [InlineData("USED_ACCEPTABLE", "Used - Fair", "Fair", "UsedAcceptable")]
    [InlineData("FOR_PARTS_OR_NOT_WORKING", "Used - Fair", "Poor", "UsedAcceptable")]
    public void MapCondition_TranslatesEbayGradesToEachSitesVocabulary(
        string ebay, string facebook, string mercari, string amazon)
    {
        Assert.Equal(facebook, CrossListingExporter.MapCondition("facebook", ebay));
        Assert.Equal(mercari, CrossListingExporter.MapCondition("mercari", ebay));
        Assert.Equal(amazon, CrossListingExporter.MapCondition("amazon", ebay));
    }

    // ── Marketplace-specific warnings ─────────────────────────────────────────

    [Fact]
    public void Amazon_WithoutAGtin_WarnsBeforeTheSellerWastesAnUpload()
    {
        var req = SampleDraft();
        req.Upc = req.Ean = req.Isbn = "";

        var amazon = For(NewExporter().Export(req), "amazon");

        Assert.Contains(amazon.Warnings, w => w.Contains("UPC, EAN or ISBN"));
        Assert.Contains(amazon.Fields, f => f.Name == "Product ID (GTIN)" && f.Required && f.Value == "");
    }

    [Fact]
    public void Amazon_WithAUpc_EmitsAmazonsNumericProductIdTypeCode()
    {
        var amazon = For(NewExporter().Export(SampleDraft()), "amazon");

        Assert.Equal("012345678905", CsvValue(amazon.Csv, "product-id"));
        Assert.Equal("3", CsvValue(amazon.Csv, "product-id-type"));   // 3 = UPC
        Assert.DoesNotContain(amazon.Warnings, w => w.Contains("UPC, EAN or ISBN"));
    }

    [Fact]
    public void Mercari_WithoutAWeight_WarnsThatShippingWillBeUnderCharged()
    {
        var req = SampleDraft();
        req.WeightLbs = req.WeightOz = 0m;

        var mercari = For(NewExporter().Export(req), "mercari");

        Assert.Contains(mercari.Warnings, w => w.Contains("weight", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Export_WithLocalOnlyPhotoUrls_WarnsTheyCannotBeImported()
    {
        var req = SampleDraft();
        req.ImageUrls = ["/photos/local-1.jpg"];

        var export = NewExporter().Export(req);

        Assert.Contains(export.Warnings, w => w.Contains("local app URLs"));
    }

    [Fact]
    public void Export_WithNoPhotos_WarnsOnce()
    {
        var req = SampleDraft();
        req.ImageUrls = [];

        var export = NewExporter().Export(req);

        Assert.Contains(export.Warnings, w => w.Contains("no photos", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(export.Warnings, w => w.Contains("local app URLs"));
    }

    // ── CSV ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Csv_HasOneValuePerHeaderForEveryMarketplace()
    {
        foreach (var listing in NewExporter().Export(SampleDraft()).Listings)
        {
            var lines = listing.Csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            Assert.Equal(lines[0].Split(',').Length, SplitCsvRow(lines[1]).Count);
        }
    }

    [Fact]
    public void Csv_QuotesCommasAndEscapesEmbeddedQuotes()
    {
        var req = SampleDraft();
        req.Title = "Miner, 104TH, \"Pro\" model";

        var mercari = For(NewExporter().Export(req), "mercari");

        Assert.Contains("\"Miner, 104TH, \"\"Pro\"\" model\"", mercari.Csv);
        Assert.Equal("Miner, 104TH, \"Pro\" model", CsvValue(mercari.Csv, "title"));
    }

    [Fact]
    public void Csv_NeutralizesSpreadsheetFormulasButLeavesNumbersAlone()
    {
        var req = SampleDraft();
        req.Title = "=HYPERLINK(\"http://evil\",\"click\")";

        var mercari = For(NewExporter().Export(req), "mercari");

        Assert.StartsWith("'=", CsvValue(mercari.Csv, "title"));
        // The price column is a plain number and must not pick up an apostrophe.
        Assert.Equal(mercari.NetParityPrice.ToString("0.00"), CsvValue(mercari.Csv, "price"));
    }

    [Fact]
    public void Csv_FacebookRowUsesTheCatalogConditionTokenAndCurrencySuffixedPrice()
    {
        var facebook = For(NewExporter().Export(SampleDraft()), "facebook");

        Assert.Equal("used_good", CsvValue(facebook.Csv, "condition"));
        Assert.EndsWith(" USD", CsvValue(facebook.Csv, "price"));
        Assert.Equal("in stock", CsvValue(facebook.Csv, "availability"));
    }

    // ── SKU ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Sku_IsDeterministicAndSharedAcrossMarketplaces()
    {
        var first = NewExporter().Export(SampleDraft());
        var second = NewExporter().Export(SampleDraft());

        Assert.Equal(first.Sku, second.Sku);
        Assert.StartsWith("ING-", first.Sku);
        // Same SKU in every export row keeps the seller's inventory reconcilable across sites.
        foreach (var listing in first.Listings)
            Assert.Contains(first.Sku, listing.Csv);
    }

    [Fact]
    public void Export_EmptyDraft_DoesNotThrowAndWarnsAboutTheTitle()
    {
        var export = NewExporter().Export(new CrossListRequest());

        Assert.Equal(3, export.Listings.Count);
        Assert.Contains(export.Warnings, w => w.Contains("no title"));
        Assert.All(export.Listings, l => Assert.False(string.IsNullOrEmpty(l.Csv)));
    }

    // Reads one generated CSV's single data row by column name, so adding a column to an export
    // format doesn't break every assertion that happens to sit to the right of it.
    private static string CsvValue(string csv, string header)
    {
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var index = Array.IndexOf(lines[0].Split(','), header);
        Assert.True(index >= 0, $"CSV has no '{header}' column. Headers: {lines[0]}");
        return SplitCsvRow(lines[1])[index];
    }

    // Minimal RFC 4180 reader — good enough to assert against a single generated row.
    private static List<string> SplitCsvRow(string line)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else current.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { cells.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }

        cells.Add(current.ToString());
        return cells;
    }
}
