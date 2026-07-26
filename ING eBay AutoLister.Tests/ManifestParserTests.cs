using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// A manifest is read to decide whether to spend real money on a pallet, so the cases that matter
// most here are the ones where a plausible-looking misread produces a confident wrong number:
// a totals row counted as an item, an extended-retail column read as a unit price, or a model
// year read as a quantity.
public class ManifestParserTests
{
    private const string SimpleCsv = """
        Description,Qty,Unit Retail,Condition
        DeWalt DCD771C2 20V Drill Kit,4,169.00,Customer Return
        Ninja BL610 Blender,6,89.99,Shelf Pull
        """;

    // ── Delimited tables ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_ReadsAHeaderedCsv()
    {
        var result = ManifestParser.Parse(SimpleCsv);

        Assert.Equal("csv", result.Format);
        Assert.Equal(2, result.Lines.Count);
        Assert.Equal("DeWalt DCD771C2 20V Drill Kit", result.Lines[0].Description);
        Assert.Equal(4, result.Lines[0].Quantity);
        Assert.Equal(169.00m, result.Lines[0].UnitRetail);
        Assert.Equal("Customer Return", result.Lines[0].Condition);
    }

    [Fact]
    public void Parse_ReadsTabSeparatedPasteFromASpreadsheet()
    {
        var result = ManifestParser.Parse(
            "Item\tQuantity\tMSRP\nSony WH-1000XM4 Headphones\t2\t$348.00\nInstant Pot Duo 6qt\t5\t$99.95");

        Assert.Equal("delimited", result.Format);
        Assert.Equal(2, result.Lines.Count);
        Assert.Equal(2, result.Lines[0].Quantity);
        Assert.Equal(348.00m, result.Lines[0].UnitRetail);
    }

    [Fact]
    public void Parse_ReadsAPipeTable()
    {
        var result = ManifestParser.Parse(
            "Product | Qty | Retail\nKeurig K-Classic | 3 | 129.99\nAnker PowerCore 10000 | 20 | 25.99");

        Assert.Equal(2, result.Lines.Count);
        Assert.Equal(20, result.Lines[1].Quantity);
    }

    [Fact]
    public void Parse_HonoursQuotedFieldsContainingCommas()
    {
        var result = ManifestParser.Parse(
            "Description,Qty,Retail\n\"Drill, Impact Driver and Charger Kit\",2,199.00");

        Assert.Single(result.Lines);
        Assert.Equal("Drill, Impact Driver and Charger Kit", result.Lines[0].Description);
        Assert.Equal(2, result.Lines[0].Quantity);
    }

    // A manifest's last row is nearly always a total. Counting it as an item is how a lot gets
    // valued at roughly twice what it holds.
    [Fact]
    public void Parse_SkipsTotalsRows()
    {
        var result = ManifestParser.Parse(SimpleCsv + "\nTOTAL,10,4021.55,\nGrand Total,,,");

        Assert.Equal(2, result.Lines.Count);
        Assert.DoesNotContain(result.Lines, l => l.Description.Contains("otal"));
        Assert.Equal(2, result.RowsSkipped);
    }

    // "Extended Retail" is the line total. Read as a unit price it multiplies the whole lot's
    // claimed value by its quantity.
    [Fact]
    public void Parse_DividesExtendedRetailByQuantityWhenNoUnitPriceColumnExists()
    {
        var result = ManifestParser.Parse(
            "Description,Qty,Extended Retail\nNinja BL610 Blender,6,539.94");

        Assert.Single(result.Lines);
        Assert.Equal(89.99m, result.Lines[0].UnitRetail);
    }

    [Fact]
    public void Parse_PrefersTheUnitRetailColumnOverTheExtendedOne()
    {
        var result = ManifestParser.Parse(
            "Description,Qty,Unit Retail,Extended Retail\nNinja BL610 Blender,6,89.99,539.94");

        Assert.Equal(89.99m, result.Lines[0].UnitRetail);
    }

    [Fact]
    public void Parse_ReadsBrandAndModelColumnsIntoTheSearchQuery()
    {
        var result = ManifestParser.Parse(
            "Description,Brand,Model,Qty\nCORDLESS DRILL/DRIVER KIT,DeWalt,DCD771C2,4");

        var line = result.Lines[0];
        Assert.Equal("DeWalt", line.Brand);
        Assert.Equal("DCD771C2", line.Model);
        Assert.Contains("DeWalt", line.SearchQuery);
        Assert.Contains("DCD771C2", line.SearchQuery);
    }

    [Fact]
    public void Parse_DoesNotRepeatABrandTheDescriptionAlreadyNames()
    {
        var result = ManifestParser.Parse(
            "Description,Brand,Qty\nDeWalt 20V Drill Kit,DeWalt,2");

        Assert.Equal("DeWalt 20V Drill Kit", result.Lines[0].SearchQuery);
    }

    [Fact]
    public void Parse_DefaultsMissingQuantityToOne()
    {
        var result = ManifestParser.Parse("Description,Qty,Retail\nSingle odd item,,49.99");

        Assert.Equal(1, result.Lines[0].Quantity);
    }

    [Fact]
    public void Parse_ReadsUpcColumn()
    {
        var result = ManifestParser.Parse(
            "Description,UPC,Qty\nAnker PowerCore 10000,848061081862,20");

        Assert.Equal("848061081862", result.Lines[0].Upc);
    }

    // A description full of commas must not be mistaken for a CSV — the delimiter only counts if
    // it produces a consistent column count.
    [Fact]
    public void Parse_DoesNotShatterProseOnItsCommas()
    {
        var result = ManifestParser.Parse(
            "Estate lot: tools, kitchenware, and a few small appliances, all untested");

        Assert.NotEqual("csv", result.Format);
        Assert.Single(result.Lines);
    }

    [Fact]
    public void Parse_ReadsAHeaderlessTableByItsContents()
    {
        var result = ManifestParser.Parse(
            "DeWalt DCD771C2 Drill Kit,4,$169.00\nNinja BL610 Blender,6,$89.99\nKeurig K-Classic,3,$129.99");

        Assert.Equal(3, result.Lines.Count);
        Assert.Equal("DeWalt DCD771C2 Drill Kit", result.Lines[0].Description);
        Assert.Equal(4, result.Lines[0].Quantity);
        Assert.Equal(169.00m, result.Lines[0].UnitRetail);
        Assert.Contains("no header row", result.Note);
    }

    // ── Free-text lists ──────────────────────────────────────────────────────

    [Fact]
    public void Parse_ReadsALeadingMultiplierQuantity()
    {
        var result = ManifestParser.Parse("3x Sony WH-1000XM4 headphones\n2 x Instant Pot Duo 6qt");

        Assert.Equal("list", result.Format);
        Assert.Equal(3, result.Lines[0].Quantity);
        Assert.Equal("Sony WH-1000XM4 headphones", result.Lines[0].Description);
        Assert.Equal(2, result.Lines[1].Quantity);
    }

    [Fact]
    public void Parse_ReadsATrailingQuantityInParentheses()
    {
        var result = ManifestParser.Parse("- Milwaukee M18 impact driver (qty 5)\n- Ryobi one+ sander (qty 2)");

        Assert.Equal(5, result.Lines[0].Quantity);
        Assert.Equal("Milwaukee M18 impact driver", result.Lines[0].Description);
    }

    // "2024 Ford F-150 tailgate" must not become 2,024 units. A bare leading number is only a
    // quantity when it is small or was written with an explicit multiplier.
    [Fact]
    public void Parse_DoesNotReadAModelYearAsAQuantity()
    {
        var result = ManifestParser.Parse("2024 Ford F-150 tailgate assembly\n1998 Nintendo Game Boy Color");

        Assert.All(result.Lines, l => Assert.Equal(1, l.Quantity));
        Assert.Contains("2024", result.Lines[0].Description);
    }

    [Fact]
    public void Parse_ReadsTheTrailingPriceOnAListLine()
    {
        var result = ManifestParser.Parse("3x Sony WH-1000XM4 headphones $348.00\n2 Instant Pot Duo $99.95");

        Assert.Equal(348.00m, result.Lines[0].UnitRetail);
        Assert.DoesNotContain("$", result.Lines[0].Description);
    }

    [Fact]
    public void Parse_SkipsSectionHeadingsWithoutLetters()
    {
        var result = ManifestParser.Parse("=====\n3x Sony WH-1000XM4\n-----");

        Assert.Single(result.Lines);
        Assert.Equal(2, result.RowsSkipped);
    }

    [Fact]
    public void Parse_ReturnsNothingForEmptyInput()
    {
        Assert.Empty(ManifestParser.Parse(null).Lines);
        Assert.Empty(ManifestParser.Parse("   ").Lines);
        Assert.Equal("none", ManifestParser.Parse("").Format);
    }

    // ── Scalar helpers ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("$1,299.00", 1299.00)]
    [InlineData("1299", 1299)]
    [InlineData("USD 89.99", 89.99)]
    [InlineData("(45.00)", -45.00)]
    public void ParseMoney_StripsFormatting(string input, double expected)
    {
        Assert.Equal((decimal)expected, ManifestParser.ParseMoney(input));
    }

    [Theory]
    [InlineData("12", 12)]
    [InlineData("1,200", 1200)]
    [InlineData("4 ea", 4)]
    [InlineData("2.0", 2)]
    public void ParseQuantity_ReadsWholeUnits(string input, int expected)
    {
        Assert.Equal(expected, ManifestParser.ParseQuantity(input));
    }

    [Fact]
    public void ParseMoney_ReturnsNullForNonNumericText()
    {
        Assert.Null(ManifestParser.ParseMoney("Customer Return"));
        Assert.Null(ManifestParser.ParseMoney(""));
    }

    [Fact]
    public void SplitRow_HandlesDoubledQuotesInsideAQuotedField()
    {
        var fields = ManifestParser.SplitRow("\"24\"\" monitor\",2,199.00", ',');

        Assert.Equal(["24\" monitor", "2", "199.00"], fields);
    }

    [Fact]
    public void Parse_CapsAVeryLongManifest()
    {
        var rows = string.Join("\n", Enumerable.Range(0, 600).Select(i => $"Item number {i},1,10.00"));
        var result = ManifestParser.Parse("Description,Qty,Retail\n" + rows);

        Assert.True(result.Lines.Count <= 400);
        Assert.True(result.Lines.Count > 100);
    }
}
