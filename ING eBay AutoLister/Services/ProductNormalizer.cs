using System.Text.RegularExpressions;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

// Turns a raw product title (a supplier item, or one SoldListings row's Title+RawJson.subtitle)
// into a NormalizedProduct — the richer, comparison-ready identity ComparableMatcher/
// MarketPriceEstimator need. Wraps ProductIdentityExtractor (brand/model/part number/category/
// voltage/capacity/generation/revision — already solid, mask-and-consume regex approach) and adds
// what it doesn't cover: abbreviation/spacing normalization, quantity/lot parsing, negative-keyword
// detection, and accessory detection.
public sealed partial class ProductNormalizer(ProductIdentityExtractor identityExtractor)
{
    // Fixed vocabulary from the spec — phrases that disqualify a listing as a genuine comparable
    // for a target product that isn't itself described the same way (e.g. a working item should
    // never be priced against a "for parts" listing). Longest-phrase-first so "empty box" wins
    // over a bare "box" false-positive.
    private static readonly (string Phrase, string Canonical)[] NegativeKeywordVocabulary =
    [
        ("for repair", "for repair"),
        ("not working", "broken"),
        ("empty box", "empty box"),
        ("box only", "empty box"),
        ("case only", "case"),
        ("cover only", "cover"),
        ("aftermarket", "compatible"),
        ("untested", "broken"),
        ("as-is", "broken"),
        ("as is", "broken"),
        ("broken", "broken"),
        ("damaged", "broken"),
        ("parts", "parts"),
        ("manual", "manual"),
        ("accessory", "accessory"),
        ("accessories", "accessory"),
        ("case", "case"),
        ("cover", "cover"),
        ("replica", "replica"),
        ("compatible", "compatible"),
        ("lot", "lot"),
    ];

    // Nouns that mean "this listing IS an accessory," used together with a missing Model/PartNumber
    // to flag IsAccessoryListing (see the spec's "product appears to be an accessory" hard-reject).
    private static readonly string[] AccessoryVocabulary =
    [
        "charger", "cable", "case", "box", "manual", "screen protector", "stylus",
        "adapter", "dock", "stand", "mount", "replacement part", "spare part",
    ];

    private static readonly string[] ColorVocabulary =
    [
        "space gray", "space grey", "rose gold", "midnight", "starlight", "graphite",
        "black", "white", "silver", "gold", "gray", "grey", "blue", "red", "green",
        "purple", "pink", "yellow", "orange", "titanium",
    ];

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex CamelBoundaryRegex();

    // "lot of 10", "(10)", "10x", "10 pcs/pieces/units" — first match wins, default 1.
    [GeneratedRegex(@"\blot\s*of\s*(\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LotOfRegex();
    [GeneratedRegex(@"^\s*\((\d+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingParenCountRegex();
    [GeneratedRegex(@"\b(\d+)\s*x\b", RegexOptions.IgnoreCase)]
    private static partial Regex CountXRegex();
    [GeneratedRegex(@"\b(\d+)\s*(?:pcs|pieces|units)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CountUnitsRegex();

    [GeneratedRegex(@"\b(?:i[3579]-\d{3,5}[A-Za-z]*|Ryzen\s*[3579]|Snapdragon\s*\d+|M[1-4]\s*(?:Pro|Max|Ultra)?|Apple\s*M[1-4])\b", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessorRegex();
    [GeneratedRegex(@"\b(\d+)\s*(?:GB|TB)\s*(?:RAM|Memory)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RamRegex();
    [GeneratedRegex(@"\b(\d+\s*(?:GB|TB))\s*(?:SSD|HDD|Storage|SSHD|NVMe)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StorageRegex();
    [GeneratedRegex(@"\b(\d+(?:\.\d+)?)\s*(?:mm|inch|in|""|')\b", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    public NormalizedProduct Normalize(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return new NormalizedProduct { RawText = "" };

        // Split merged words like "ProMax" -> "Pro Max" before handing off to the extractor —
        // improves overlap between differently-formatted titles for the same product.
        var expanded = CamelBoundaryRegex().Replace(rawText, " ");

        var identity = identityExtractor.Extract(expanded);

        var product = new NormalizedProduct
        {
            Brand = identity.Brand,
            Model = identity.Model,
            PartNumber = identity.PartNumber,
            Category = identity.Category,
            Capacity = identity.Capacity,
            Generation = identity.Generation,
            Voltage = identity.Voltage,
            Revision = identity.Revision,
            Condition = identity.ConditionKeywords.Count > 0 ? identity.ConditionKeywords[0] : null,
            RawText = expanded,
        };

        // Best-effort extras the spec asks for that ProductIdentityExtractor doesn't cover
        // (it's tuned for ASIC miners / industrial automation, not consumer electronics specs).
        var processorMatch = ProcessorRegex().Match(expanded);
        if (processorMatch.Success) product.Processor = processorMatch.Value.Trim();

        var ramMatch = RamRegex().Match(expanded);
        if (ramMatch.Success) product.Ram = $"{ramMatch.Groups[1].Value}GB RAM".Replace(" ", "");

        var storageMatch = StorageRegex().Match(expanded);
        if (storageMatch.Success) product.Storage = storageMatch.Groups[1].Value.Replace(" ", "");

        var sizeMatch = SizeRegex().Match(expanded);
        if (sizeMatch.Success) product.Size = sizeMatch.Value.Trim();

        var lowerText = expanded.ToLowerInvariant();
        product.Color = ColorVocabulary.FirstOrDefault(c => ContainsWord(lowerText, c));

        product.Quantity = ExtractQuantity(expanded);

        var negatives = new List<string>();
        foreach (var (phrase, canonical) in NegativeKeywordVocabulary)
        {
            if (ContainsWord(lowerText, phrase) && !negatives.Contains(canonical))
                negatives.Add(canonical);
        }
        product.NegativeKeywords = negatives;

        var accessories = AccessoryVocabulary.Where(a => ContainsWord(lowerText, a)).ToList();
        product.Accessories = accessories;
        // An accessory word only means "this listing IS an accessory" when it's the subject of
        // the title, not a bundled extra mentioned in passing ("...S19j Pro 104TH with case and
        // box"). Requiring Brand/Category to also be missing had it backwards: a real accessory
        // almost always names its host product's brand ("iPad Screen Protector", "Antminer Power
        // Cable"), so that brand naturally gets recognized — it doesn't mean the listing isn't an
        // accessory. Position is the better signal: a leading accessory word ("Screen Protector
        // for iPad...") is the product being sold; one trailing after "with"/"and"/"+"/"includes"
        // is an included extra on a listing for something else.
        product.IsAccessoryListing = accessories.Any(a => IsLeadingAccessoryMention(lowerText, a));

        product.ImportantKeywords = MarketplaceMatcher.ImportantWords(MarketplaceMatcher.Normalize(expanded));

        return product;
    }

    private static int ExtractQuantity(string text)
    {
        var m = LotOfRegex().Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var lotQty) && lotQty > 0) return lotQty;

        m = LeadingParenCountRegex().Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var parenQty) && parenQty > 0) return parenQty;

        m = CountXRegex().Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var xQty) && xQty > 0) return xQty;

        m = CountUnitsRegex().Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var unitsQty) && unitsQty > 0) return unitsQty;

        return 1;
    }

    // Whole-word/phrase containment — avoids "case" matching inside "lowercase" or "showcase".
    private static bool ContainsWord(string lowerHaystack, string lowerNeedle)
    {
        var index = 0;
        while ((index = lowerHaystack.IndexOf(lowerNeedle, index, StringComparison.Ordinal)) >= 0)
        {
            var beforeOk = index == 0 || !char.IsLetterOrDigit(lowerHaystack[index - 1]);
            var afterIdx = index + lowerNeedle.Length;
            var afterOk = afterIdx >= lowerHaystack.Length || !char.IsLetterOrDigit(lowerHaystack[afterIdx]);
            if (beforeOk && afterOk) return true;
            index += lowerNeedle.Length;
        }
        return false;
    }

    private static readonly string[] BundleMarkers = ["with", "and", "&", "+", "plus", "includes", "include", "comes"];

    // True when an accessory word is the subject of the title (leading position, e.g. "Screen
    // Protector for iPad...") rather than a bundled extra mentioned in passing (e.g. "...104TH
    // with case and box", where "case"/"box" trail a bundling marker like "with"/"and").
    private static bool IsLeadingAccessoryMention(string lowerText, string lowerNeedle)
    {
        var index = 0;
        while ((index = lowerText.IndexOf(lowerNeedle, index, StringComparison.Ordinal)) >= 0)
        {
            var beforeOk = index == 0 || !char.IsLetterOrDigit(lowerText[index - 1]);
            var afterIdx = index + lowerNeedle.Length;
            var afterOk = afterIdx >= lowerText.Length || !char.IsLetterOrDigit(lowerText[afterIdx]);
            if (beforeOk && afterOk)
            {
                var wordsBefore = lowerText[..index].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var precededByBundleMarker = wordsBefore.Length > 0 && BundleMarkers.Contains(wordsBefore[^1].Trim(',', '-', '/'));
                var isLeading = wordsBefore.Length <= 4;
                if (!precededByBundleMarker && isLeading) return true;
            }
            index += lowerNeedle.Length;
        }
        return false;
    }
}
