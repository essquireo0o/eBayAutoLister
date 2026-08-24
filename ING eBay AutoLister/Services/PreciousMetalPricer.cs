using System.Globalization;
using System.Text.RegularExpressions;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// What the metal in a lot is worth, from the spot price and a weight.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> On 2026-08-23 a live show offered a <i>natural gold nugget 2.53 gram</i>
/// and the board said "resells around $260 — bid up to $125.15", off nineteen sold comps it had
/// widened to plain "natural gold nugget" because nothing had sold under the full name. Gold was
/// $4,604/oz that minute, so 2.53 grams of it was worth $318 at a nugget's usual purity and $375
/// pure. The advice was to stop bidding at a third of the metal.
/// </para>
/// <para>
/// Comps were the wrong instrument, not a badly-tuned one. "Natural gold nugget" describes lots
/// from a tenth of a gram to a hundred grams; their sale prices have no central tendency, which is
/// exactly what "prices too scattered to trust" was reporting. The invariant for a commodity is
/// price per gram, and it is published every minute. The owner put it plainly: "that is a very easy
/// calculation on google."
/// </para>
/// <para>
/// <b>What it refuses to do.</b> Plated, filled and vermeil carry a few cents of metal on a base of
/// brass, and pricing one at melt would be the same mistake in the opposite direction and far more
/// expensive. Those are rejected outright, by <see cref="Bullion.Grade"/> — the same reading that
/// decides which comps may price a bullion row, so the two can never disagree about what solid
/// metal is. Purity that is not stated is never assumed
/// either: an unmarked nugget gets a RANGE with its assumption named, not a single confident
/// number, because inventing a purity is inventing money.
/// </para>
/// </remarks>
public sealed partial class PreciousMetalPricer(IHttpClientFactory http, ActionLog log)
{
    public const decimal GramsPerTroyOunce = 31.1034768m;
    private const decimal GramsPerPennyweight = 1.55517384m;
    private const decimal GramsPerGrain = 0.06479891m;

    /// <summary>Spot moves by the second; a lot is priced in a few. One read a minute is plenty.</summary>
    private static readonly TimeSpan SpotFreshFor = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, (decimal PerGram, DateTimeOffset At)> _spot = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    // ── What the name says ────────────────────────────────────────────────────────

    private static readonly (string Word, string Symbol)[] Metals =
    [
        ("palladium", "XPD"), ("platinum", "XPT"), ("silver", "XAG"), ("gold", "XAU"),
    ];

    [GeneratedRegex(@"(?<n>\d+(?:\.\d+)?)\s*(?<u>grams?|gm?s?\b|g\b|ozt\b|oz\.?t\b|troy\s*ounces?|ounces?|oz\b|dwt\b|pennyweights?|grains?|gr\b|kilograms?|kg\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WeightRx();

    [GeneratedRegex(@"\b(?<k>10|12|14|18|21|22|24)\s*(?:k|kt|karat|carat)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KaratRx();

    [GeneratedRegex(@"(?<![\d.])(?:\.(?<dec>9\d{2,3})|\b(?<whole>9\d{2})\b)(?![\d])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FinenessRx();

    /// <summary>The metal, weight and purity a lot name actually states. Null when it is not bullion.</summary>
    public MetalContent? Read(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var text = " " + name.ToLowerInvariant().Replace('⁄', '/') + " ";

        // A layer of gold on brass is not gold, and this check comes first and is absolute.
        //
        // The vocabulary lives in Bullion, not here. It was duplicated in this file until
        // 2026-08-24, and the copy was wrong in two ways that list had already reasoned through:
        // it treated bare "tone" as plating, which reads a rainbow-TONED Morgan dollar as costume
        // brass, and bare "plate" the same way, which does it to a sterling serving plate. One
        // vocabulary, so the melt price and the comp filter can never disagree about what solid
        // metal is. Anything short of Solid — a coating, or a title too vague to commit either way
        // — gets no melt figure at all.
        if (Bullion.Grade(name) is not BullionGrade.Solid) return null;

        var metal = Metals.FirstOrDefault(m => text.Contains(m.Word, StringComparison.Ordinal));
        if (metal.Symbol is null) return null;

        var w = WeightRx().Match(text);
        if (!w.Success) return null;
        if (!decimal.TryParse(w.Groups["n"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var qty) || qty <= 0)
            return null;

        var unit = w.Groups["u"].Value.Trim().TrimEnd('.');
        var grams = unit switch
        {
            var u when u.StartsWith("kg") || u.StartsWith("kilogram") => qty * 1000m,
            var u when u.StartsWith("dwt") || u.StartsWith("pennyweight") => qty * GramsPerPennyweight,
            var u when u.StartsWith("grain") || u == "gr" => qty * GramsPerGrain,
            // "oz" on a precious-metal lot is the TROY ounce — 31.10g, not 28.35. Getting this
            // wrong understates an ounce of gold by about ten percent, which on a live show is
            // the difference between winning and losing the lot.
            var u when u.Contains("oz") || u.Contains("ounce") => qty * GramsPerTroyOunce,
            _ => qty,
        };

        if (grams is <= 0 or > 100_000m) return null;   // a typo, not a lot

        var (low, high, note) = Purity(text, metal.Symbol);
        return new MetalContent(metal.Symbol, Title(metal.Word), grams, low, high, note);

        static string Title(string w) => char.ToUpperInvariant(w[0]) + w[1..];
    }

    /// <summary>
    /// The fraction of the weight that is the metal — as a range, because most lots do not say.
    /// </summary>
    private static (decimal Low, decimal High, string Note) Purity(string text, string symbol)
    {
        if (KaratRx().Match(text) is { Success: true } k)
        {
            var karat = decimal.Parse(k.Groups["k"].Value, CultureInfo.InvariantCulture) / 24m;
            return (karat, karat, $"{k.Groups["k"].Value}k as stated");
        }

        if (FinenessRx().Match(text) is { Success: true } f)
        {
            var digits = f.Groups["dec"].Success ? f.Groups["dec"].Value : f.Groups["whole"].Value;
            var fine = decimal.Parse(digits, CultureInfo.InvariantCulture) / (digits.Length == 3 ? 1000m : 10000m);
            if (fine is > 0.5m and <= 1m) return (fine, fine, $".{digits} fine as stated");
        }

        if (text.Contains("sterling", StringComparison.Ordinal))
            return (0.925m, 0.925m, "sterling (.925)");
        if (text.Contains("fine ", StringComparison.Ordinal) || text.Contains("bullion", StringComparison.Ordinal))
            return (0.999m, 0.999m, "fine bullion (.999)");
        if (text.Contains("coin silver", StringComparison.Ordinal))
            return (0.900m, 0.900m, "coin silver (.900)");

        // Nothing stated. A natural nugget is typically 80–95% gold and never refined, so a range
        // is the honest answer; anything else is priced pure with that said out loud, because an
        // unmarked bar could be anything and the seller has to know the number is a ceiling.
        if (symbol == "XAU" && (text.Contains("nugget", StringComparison.Ordinal)
                             || text.Contains("natural", StringComparison.Ordinal)
                             || text.Contains("placer", StringComparison.Ordinal)
                             || text.Contains("flake", StringComparison.Ordinal)))
            return (0.80m, 0.95m, "purity not stated — natural gold is usually 80–95%");

        return (1m, 1m, "purity not stated — priced as pure, so this is a ceiling");
    }

    // ── What the metal costs right now ────────────────────────────────────────────

    /// <summary>Spot per gram in USD, or null when the price could not be read.</summary>
    public async Task<decimal?> SpotPerGramAsync(string symbol, CancellationToken ct = default)
    {
        if (_spot.TryGetValue(symbol, out var held) && DateTimeOffset.UtcNow - held.At < SpotFreshFor)
            return held.PerGram;

        await _gate.WaitAsync(ct);
        try
        {
            if (_spot.TryGetValue(symbol, out held) && DateTimeOffset.UtcNow - held.At < SpotFreshFor)
                return held.PerGram;

            var client = http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(12);
            using var doc = System.Text.Json.JsonDocument.Parse(
                await client.GetStringAsync($"https://api.gold-api.com/price/{symbol}", ct));

            if (!doc.RootElement.TryGetProperty("price", out var p)
                || p.ValueKind != System.Text.Json.JsonValueKind.Number)
                return null;

            var perOunce = p.GetDecimal();
            if (perOunce <= 0) return null;

            var perGram = perOunce / GramsPerTroyOunce;
            _spot[symbol] = (perGram, DateTimeOffset.UtcNow);
            log.Add("Info", "Metal spot price read", $"{symbol} ${perOunce:N2}/ozt (${perGram:N2}/g)");
            return perGram;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A live show does not stop because a price feed did. The comps answer still stands;
            // it simply does not get the melt floor under it.
            log.Add("Warning", "Metal spot price unavailable", $"{symbol}: {ex.Message}");
            return null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>The melt value of what the name describes, or null when it is not a metal lot.</summary>
    public async Task<MetalMelt?> ValueAsync(string? name, CancellationToken ct = default)
    {
        if (Read(name) is not { } content) return null;
        if (await SpotPerGramAsync(content.Symbol, ct) is not { } perGram) return null;

        return new MetalMelt(
            content, perGram,
            Math.Round(content.Grams * content.PurityLow * perGram, 2),
            Math.Round(content.Grams * content.PurityHigh * perGram, 2),
            DateTimeOffset.UtcNow);
    }
}

/// <summary>The metal a lot name states it contains.</summary>
public sealed record MetalContent(
    string Symbol, string Metal, decimal Grams,
    decimal PurityLow, decimal PurityHigh, string PurityNote);

/// <summary>What that metal is worth at the current spot price.</summary>
public sealed record MetalMelt(
    MetalContent Content, decimal SpotPerGram, decimal MeltLow, decimal MeltHigh, DateTimeOffset AsOf)
{
    public bool PurityIsKnown => Content.PurityLow == Content.PurityHigh;

    /// <summary>One line a person can check against Google in five seconds.</summary>
    public string Arithmetic =>
        PurityIsKnown
            ? $"{Content.Grams:0.##} g × {Content.PurityLow:P0} {Content.Metal.ToLowerInvariant()} "
              + $"at ${SpotPerGram:N2}/g = ${MeltLow:N2} of metal"
            : $"{Content.Grams:0.##} g × {Content.PurityLow:P0}–{Content.PurityHigh:P0} "
              + $"{Content.Metal.ToLowerInvariant()} at ${SpotPerGram:N2}/g = ${MeltLow:N2}–${MeltHigh:N2} of metal";
}
