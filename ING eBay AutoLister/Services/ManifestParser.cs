using System.Globalization;
using System.Text.RegularExpressions;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

public sealed class ManifestParseResult
{
    /// <summary>csv | delimited | list | none</summary>
    public string Format { get; set; } = "none";
    /// <summary>What was actually recognised, in the seller's words — shown in the UI.</summary>
    public string Note { get; set; } = "";
    public List<ManifestLine> Lines { get; set; } = [];
    /// <summary>Rows that looked like totals/headers/notes and were deliberately skipped.</summary>
    public int RowsSkipped { get; set; }
}

/// <summary>
/// Reads a pasted liquidation manifest without spending a model call.
///
/// Most real manifests arrive as a spreadsheet export — a CSV, a TSV pasted straight out of
/// Excel, or a pipe table copied from an email. Those have columns, and columns can be read
/// exactly: a parser cannot hallucinate a quantity or invent a line that was never on the
/// pallet, and a 400-row manifest costs nothing to read. Claude is the fallback for the cases
/// this genuinely cannot do — a photo of a printed manifest, or a prose lot description
/// ("estate lot: two Dewalt drills, a box of assorted cables…").
///
/// Everything here is pure and deterministic.
/// </summary>
public static class ManifestParser
{
    private const int MaxRows = 400;

    // Header keywords per field, most specific first — "unit retail" must beat "extended retail"
    // when both columns exist, or every line's value is silently multiplied by its quantity.
    private static readonly string[][] DescriptionHeaders =
    [
        ["item description", "product description", "description", "item name", "product name"],
        ["product", "item", "title", "name", "goods", "merchandise", "desc"],
    ];

    private static readonly string[][] QuantityHeaders =
    [
        ["quantity", "qty", "units", "unit count", "pcs", "pieces", "count", "cases", "each"],
    ];

    private static readonly string[][] UnitRetailHeaders =
    [
        ["unit retail", "retail each", "unit price", "unit msrp", "msrp", "srp", "list price", "unit cost"],
        ["retail", "price", "value", "cost"],
    ];

    private static readonly string[][] ExtendedRetailHeaders =
    [
        ["extended retail", "ext retail", "ext. retail", "total retail", "extended price", "ext price", "line total", "total value", "extended"],
    ];

    private static readonly string[][] ConditionHeaders =
    [
        ["condition", "cond", "grade", "cond.", "item condition", "disposition"],
    ];

    private static readonly string[][] BrandHeaders =
    [
        ["brand", "manufacturer", "mfr", "mfg", "vendor", "make"],
    ];

    private static readonly string[][] ModelHeaders =
    [
        ["model", "mpn", "part number", "part no", "sku", "item number", "item #", "style"],
    ];

    private static readonly string[][] UpcHeaders =
    [
        ["upc", "ean", "gtin", "barcode", "upc/ean"],
    ];

    // A manifest's last rows are almost always totals. Counting "GRAND TOTAL  412  $18,204.00"
    // as an item is how a lot gets valued at twice what it holds.
    private static readonly Regex TotalsRow = new(
        @"^\s*(grand\s+)?(total|totals|subtotal|sub-total|sum|manifest\s+total|pallet\s+total)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "3x Dewalt DCD771 drill $89.99", "2 x ...", "Qty 4 - ...", "- 1 ..."
    private static readonly Regex LeadingQuantity = new(
        @"^\s*(?:[-*•]\s*)?(?:qty\.?\s*[:=]?\s*)?(\d{1,4})\s*(?:x|×|@|pcs?|pieces?|units?|ea\.?)?\s*[-–—:.)]?\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "... (qty 4)", "... x12" at the end, "... - 6 units"
    private static readonly Regex TrailingQuantity = new(
        @"[\(\[]?\s*(?:qty\.?|quantity)\s*[:=]?\s*(\d{1,4})\s*[\)\]]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MoneyToken = new(
        @"(?<![\w.])\$\s*([0-9][0-9,]*(?:\.[0-9]{1,2})?)|(?:^|\s)([0-9][0-9,]*\.[0-9]{2})(?![\w.])",
        RegexOptions.Compiled);

    private static readonly Regex UpcToken = new(@"\b(\d{12,14})\b", RegexOptions.Compiled);

    public static ManifestParseResult Parse(string? text)
    {
        var result = new ManifestParseResult();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var rawLines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Take(MaxRows + 40)
            .ToList();
        if (rawLines.Count == 0) return result;

        var delimiter = DetectDelimiter(rawLines);
        if (delimiter is not null)
        {
            var table = rawLines.Select(l => SplitRow(l, delimiter.Value)).ToList();
            var parsed = ParseTable(table, delimiter.Value);
            if (parsed.Lines.Count > 0) return parsed;
        }

        return ParseFreeList(rawLines);
    }

    // ── Delimited tables ─────────────────────────────────────────────────────

    /// <summary>
    /// A delimiter only counts if it produces the SAME column count on most rows. A prose
    /// description full of commas would otherwise look like a CSV and shatter into nonsense.
    /// </summary>
    private static char? DetectDelimiter(List<string> lines)
    {
        var sample = lines.Take(25).ToList();
        char? best = null;
        var bestScore = 0;

        foreach (var candidate in new[] { '\t', '|', ',', ';' })
        {
            var counts = sample.Select(l => SplitRow(l, candidate).Count).ToList();
            var modal = counts.GroupBy(c => c).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First();
            if (modal.Key < 2) continue;

            // Rows agreeing on the modal column count, weighted up for wider tables — a real
            // manifest has a description, a quantity and a price, not two columns of prose.
            var agreeing = modal.Count();
            if (agreeing < Math.Max(2, sample.Count / 2)) continue;

            var score = agreeing * 10 + Math.Min(modal.Key, 8);
            if (score > bestScore) { bestScore = score; best = candidate; }
        }

        return best;
    }

    /// <summary>RFC 4180 style split — quoted fields may contain the delimiter and doubled quotes.</summary>
    public static List<string> SplitRow(string line, char delimiter)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = false;
                }
                else current.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == delimiter) { fields.Add(current.ToString().Trim()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString().Trim());
        return fields;
    }

    private static ManifestParseResult ParseTable(List<List<string>> table, char delimiter)
    {
        var result = new ManifestParseResult
        {
            Format = delimiter == ',' ? "csv" : "delimited",
        };

        var width = table.GroupBy(r => r.Count).OrderByDescending(g => g.Count()).First().Key;
        var rows = table.Where(r => r.Count >= Math.Min(2, width)).ToList();

        // ── Header ──────────────────────────────────────────────────────────
        var headerIndex = -1;
        Columns? cols = null;
        for (var i = 0; i < Math.Min(rows.Count, 8); i++)
        {
            var candidate = MapColumns(rows[i]);
            if (candidate.Description >= 0 && (candidate.Quantity >= 0 || candidate.UnitRetail >= 0 || candidate.ExtendedRetail >= 0))
            {
                headerIndex = i;
                cols = candidate;
                break;
            }
        }

        var describedByHeader = cols is not null;
        cols ??= GuessColumns(rows);
        if (cols is null || cols.Description < 0) return result;

        var body = rows.Skip(headerIndex + 1).ToList();
        var lines = new List<ManifestLine>();

        foreach (var row in body)
        {
            if (lines.Count >= MaxRows) break;
            var description = Field(row, cols.Description);
            if (string.IsNullOrWhiteSpace(description)) { result.RowsSkipped++; continue; }
            if (TotalsRow.IsMatch(description)) { result.RowsSkipped++; continue; }
            // A row whose "description" is purely numeric is a stray total or a page number.
            if (ParseMoney(description) is not null && !description.Any(char.IsLetter)) { result.RowsSkipped++; continue; }

            var qty = ParseQuantity(Field(row, cols.Quantity)) ?? 1;
            var unitRetail = ParseMoney(Field(row, cols.UnitRetail));
            if (unitRetail is null or <= 0m)
            {
                // Extended retail is the line total; the per-unit figure is what matters, and
                // dividing is exact rather than a guess.
                var ext = ParseMoney(Field(row, cols.ExtendedRetail));
                if (ext is > 0m && qty > 0) unitRetail = Math.Round(ext.Value / qty, 2);
            }

            var line = new ManifestLine
            {
                Description = Clean(description),
                Quantity = Math.Max(1, qty),
                UnitRetail = unitRetail is > 0m ? unitRetail : null,
                Condition = Clean(Field(row, cols.Condition)),
                Brand = Clean(Field(row, cols.Brand)),
                Model = Clean(Field(row, cols.Model)),
                Upc = DigitsOnly(Field(row, cols.Upc)),
            };
            line.SearchQuery = BuildQuery(line);
            lines.Add(line);
        }

        result.Lines = lines;
        result.Note = describedByHeader
            ? $"Read {lines.Count} line{(lines.Count == 1 ? "" : "s")} from a {DelimiterName(delimiter)} manifest with column headers."
            : $"Read {lines.Count} line{(lines.Count == 1 ? "" : "s")} from a {DelimiterName(delimiter)} table — no header row found, so columns were identified by their contents.";
        return result;
    }

    private static string DelimiterName(char d) => d switch
    {
        '\t' => "tab-separated",
        '|' => "pipe-separated",
        ';' => "semicolon-separated",
        _ => "comma-separated",
    };

    private sealed class Columns
    {
        public int Description = -1;
        public int Quantity = -1;
        public int UnitRetail = -1;
        public int ExtendedRetail = -1;
        public int Condition = -1;
        public int Brand = -1;
        public int Model = -1;
        public int Upc = -1;
    }

    private static Columns MapColumns(List<string> header)
    {
        var cells = header.Select(h => h.Trim().Trim('"').ToLowerInvariant()).ToList();

        // A line-total column has to be identified BEFORE the unit-price one and then kept out of
        // it. "Extended Retail" contains the word "retail", so a plain contains-match would happily
        // read a line total as a per-unit price — which multiplies the lot's claimed value by every
        // line's quantity, in the direction that talks someone into buying.
        var extended = MatchHeader(cells, ExtendedRetailHeaders);
        var blockedFromUnitPrice = new HashSet<int>();
        if (extended >= 0) blockedFromUnitPrice.Add(extended);
        for (var i = 0; i < cells.Count; i++)
            if (LooksLikeALineTotal(cells[i])) blockedFromUnitPrice.Add(i);

        return new Columns
        {
            Description = MatchHeader(cells, DescriptionHeaders),
            Quantity = MatchHeader(cells, QuantityHeaders),
            UnitRetail = MatchHeader(cells, UnitRetailHeaders, blockedFromUnitPrice),
            ExtendedRetail = extended,
            Condition = MatchHeader(cells, ConditionHeaders),
            Brand = MatchHeader(cells, BrandHeaders),
            Model = MatchHeader(cells, ModelHeaders),
            Upc = MatchHeader(cells, UpcHeaders),
        };
    }

    private static bool LooksLikeALineTotal(string header) =>
        header.Contains("extended", StringComparison.Ordinal)
        || header.Contains("ext ", StringComparison.Ordinal)
        || header.Contains("ext.", StringComparison.Ordinal)
        || header.StartsWith("ext", StringComparison.Ordinal)
        || header.Contains("total", StringComparison.Ordinal);

    // Exact matches across every preference tier first, then contains — otherwise a column called
    // "Retail" would lose to "Retail Each" simply because the latter appears earlier in the row.
    private static int MatchHeader(List<string> cells, string[][] tiers, HashSet<int>? blocked = null)
    {
        bool Allowed(int i) => blocked is null || !blocked.Contains(i);

        foreach (var tier in tiers)
            foreach (var keyword in tier)
                for (var i = 0; i < cells.Count; i++)
                    if (Allowed(i) && cells[i] == keyword) return i;

        foreach (var tier in tiers)
            foreach (var keyword in tier)
                for (var i = 0; i < cells.Count; i++)
                    if (Allowed(i) && cells[i].Contains(keyword, StringComparison.Ordinal)) return i;

        return -1;
    }

    /// <summary>
    /// No header row: identify columns by what they hold. The widest text column is the
    /// description, a column of small whole numbers is the quantity, and a column of currency
    /// is the retail. Anything less confident than that is left unmapped rather than guessed.
    /// </summary>
    private static Columns? GuessColumns(List<List<string>> rows)
    {
        var body = rows.Take(40).ToList();
        if (body.Count == 0) return null;
        var width = body.Max(r => r.Count);
        if (width < 2) return null;

        var cols = new Columns();
        var bestTextScore = 0d;
        var bestMoneyScore = 0d;
        var bestQtyScore = 0d;

        for (var c = 0; c < width; c++)
        {
            var values = body.Select(r => Field(r, c)).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            if (values.Count == 0) continue;

            var textScore = values.Count(v => v.Count(char.IsLetter) >= 4) / (double)values.Count
                            * values.Average(v => Math.Min(v.Length, 80));
            var moneyShare = values.Count(v => ParseMoney(v) is > 0m) / (double)values.Count;
            var qtyShare = values.Count(v => ParseQuantity(v) is > 0 and <= 9999 && !v.Contains('.')) / (double)values.Count;

            if (textScore > bestTextScore) { bestTextScore = textScore; cols.Description = c; }
            if (moneyShare >= 0.6 && moneyShare > bestMoneyScore && values.Any(v => v.Contains('$') || v.Contains('.')))
            { bestMoneyScore = moneyShare; cols.UnitRetail = c; }
            if (qtyShare >= 0.7 && qtyShare > bestQtyScore) { bestQtyScore = qtyShare; cols.Quantity = c; }
        }

        // The description column can't also be the quantity or the price column.
        if (cols.Quantity == cols.Description) cols.Quantity = -1;
        if (cols.UnitRetail == cols.Description) cols.UnitRetail = -1;
        return cols.Description >= 0 ? cols : null;
    }

    // ── Free-text lists ──────────────────────────────────────────────────────

    /// <summary>
    /// A one-item-per-line list, which is how lots get described in emails, auction blurbs and
    /// Facebook posts: "3x Dewalt DCD771 drill $89.99". No columns, so quantity and price are
    /// pulled off the line itself, and the price is only trusted when it is unambiguous.
    /// </summary>
    private static ManifestParseResult ParseFreeList(List<string> rawLines)
    {
        var result = new ManifestParseResult { Format = "list" };
        var lines = new List<ManifestLine>();

        foreach (var raw in rawLines)
        {
            if (lines.Count >= MaxRows) break;
            var text = raw.Trim();
            if (text.Length < 3) { result.RowsSkipped++; continue; }
            if (TotalsRow.IsMatch(text)) { result.RowsSkipped++; continue; }
            // Section headings and prose sentences aren't items.
            if (!text.Any(char.IsLetter)) { result.RowsSkipped++; continue; }

            var qty = 1;
            var body = text;

            var lead = LeadingQuantity.Match(body);
            if (lead.Success && int.TryParse(lead.Groups[1].Value, out var leadQty) && leadQty is > 0 and <= 9999)
            {
                // "2024 Ford ..." must not be read as 2,024 units. A leading number is only a
                // quantity when it is small or was written with an explicit multiplier.
                var explicitMultiplier = lead.Value.Contains('x', StringComparison.OrdinalIgnoreCase)
                    || lead.Value.Contains('×') || lead.Value.Contains("qty", StringComparison.OrdinalIgnoreCase);
                if (explicitMultiplier || leadQty <= 200)
                {
                    qty = leadQty;
                    body = body[lead.Length..];
                }
            }
            else
            {
                var trail = TrailingQuantity.Match(body);
                if (trail.Success && int.TryParse(trail.Groups[1].Value, out var trailQty) && trailQty is > 0 and <= 9999)
                {
                    qty = trailQty;
                    body = body[..trail.Index];
                }
            }

            decimal? retail = null;
            var monies = MoneyToken.Matches(body);
            if (monies.Count > 0)
            {
                var token = monies[^1];
                var value = ParseMoney(token.Groups[1].Success ? token.Groups[1].Value : token.Groups[2].Value);
                if (value is > 0m) retail = value;
                body = body.Remove(token.Index, token.Length);
            }

            var upc = "";
            var upcMatch = UpcToken.Match(body);
            if (upcMatch.Success) upc = upcMatch.Groups[1].Value;

            var description = Clean(body);
            if (description.Length < 3 || description.Count(char.IsLetter) < 3) { result.RowsSkipped++; continue; }

            var line = new ManifestLine
            {
                Description = description,
                Quantity = Math.Max(1, qty),
                UnitRetail = retail,
                Upc = upc,
            };
            line.SearchQuery = BuildQuery(line);
            lines.Add(line);
        }

        result.Lines = lines;
        result.Note = lines.Count > 0
            ? $"Read {lines.Count} line{(lines.Count == 1 ? "" : "s")} from a plain list — one item per line."
            : "";
        if (lines.Count == 0) result.Format = "none";
        return result;
    }

    // ── Shared helpers ───────────────────────────────────────────────────────

    private static string Field(List<string> row, int index) =>
        index >= 0 && index < row.Count ? row[index].Trim().Trim('"').Trim() : "";

    public static int? ParseQuantity(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var cleaned = new string(text.Where(c => char.IsDigit(c) || c == '.' || c == ',').ToArray()).Replace(",", "");
        if (cleaned.Length == 0) return null;
        if (int.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)) return whole;
        if (decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var dec))
            return (int)Math.Round(dec, MidpointRounding.AwayFromZero);
        return null;
    }

    public static decimal? ParseMoney(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        var negative = trimmed.StartsWith('(') && trimmed.EndsWith(')');
        var cleaned = new string(trimmed.Where(c => char.IsDigit(c) || c == '.' || c == '-').ToArray());
        if (cleaned.Length == 0) return null;
        if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) return null;
        return negative ? -value : value;
    }

    private static string DigitsOnly(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "" : new string(text.Where(char.IsDigit).ToArray());

    private static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var cleaned = Regex.Replace(text, @"\s+", " ").Trim();
        return cleaned.Trim('-', '–', '—', '*', '•', ':', ';', ',', ' ').Trim();
    }

    /// <summary>
    /// The keyword string the sold-comp lookup runs on. Brand and model columns are folded in
    /// when the description doesn't already name them, because a manifest row is often just
    /// "DRILL/DRIVER KIT" with the brand sitting in its own column.
    /// </summary>
    public static string BuildQuery(ManifestLine line)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(line.Brand)
            && !line.Description.Contains(line.Brand, StringComparison.OrdinalIgnoreCase))
            parts.Add(line.Brand.Trim());

        parts.Add(line.Description.Trim());

        if (!string.IsNullOrWhiteSpace(line.Model)
            && !line.Description.Contains(line.Model, StringComparison.OrdinalIgnoreCase))
            parts.Add(line.Model.Trim());

        var query = Clean(string.Join(" ", parts.Where(p => p.Length > 0)));
        return query.Length > 120 ? query[..120].Trim() : query;
    }
}
