using System.Text;
using System.Text.RegularExpressions;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

// Joins what the seller has to what eBay wants.
//
// Three jobs, all pure and all independently wrong-able, which is why they're separate:
//
//   1. NAME MATCHING. The seller (or Claude) typed "GPU Model"; eBay's aspect is called
//      "Chipset/GPU Model". Those are the same field, and treating them as different is how a
//      listing publishes with a required specific "missing" while the value sits right there
//      under a slightly different name.
//
//   2. VALUE CANONICALISATION. On a SELECTION_ONLY aspect eBay rejects anything outside its own
//      list — "bitmain" is not "Bitmain", "used" is not "Used". The list is published, so this
//      is a lookup rather than a guess.
//
//   3. INFERENCE. This is where the clicks actually go away. eBay names the legal values for an
//      aspect; the seller has already written a title and a description. So for most aspects the
//      answer is already sitting in the seller's own text and can be found rather than typed.
//
// Everything here refuses rather than guesses. A suggestion carries its confidence and where it
// came from, and nothing is written into the listing without the seller applying it — a wrong
// Item Specific is worse than a blank one, because a blank one is visibly blank.
public static partial class AspectMatcher
{
    // ── Name matching ─────────────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlnum();

    // "Chipset/GPU Model" → "chipsetgpumodel". Case, spaces, slashes and punctuation are noise;
    // word order and the words themselves are not.
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        return NonAlnum().Replace(name.Trim().ToLowerInvariant(), "");
    }

    public static List<string> Tokens(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return [];
        return NonAlnum().Split(name.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .ToList();
    }

    // Names that mean the same field. Deliberately NOT a rename table: an alias only fires when
    // it lands on exactly one aspect this category actually has. That constraint is what makes
    // aliasing safe — "Capacity" means "Storage Capacity" in a hard-drive category and means
    // "Battery Capacity" in a power-bank one, and if a category has both, this makes no call.
    private static readonly string[][] AliasGroups =
    [
        ["brand", "manufacturer", "make", "brandname"],
        ["mpn", "manufacturerpartnumber", "partnumber", "manufacturerspartnumber", "manufacturernumber"],
        ["model", "modelname", "modelnumber", "modelno", "modelnr"],
        ["color", "colour", "primarycolor", "primarycolour", "maincolor"],
        ["type", "itemtype", "producttype", "styletype"],
        ["upc", "upccode", "universalproductcode"],
        ["ean", "eancode"],
        ["countryregionofmanufacture", "countryofmanufacture", "countryoforigin", "madein", "manufacturedin"],
        ["capacity", "storagecapacity", "totalcapacity"],
        ["material", "primarymaterial", "mainmaterial"],
        ["size", "itemsize"],
        ["style", "itemstyle"],
        ["hashrate", "hashrateths", "hashrate2", "hashingrate", "hashpower"],
        ["powerconsumption", "wattage", "powerw", "maximumpowerconsumption"],
        ["formfactor", "formfactorsize"],
        ["operatingsystem", "os"],
        ["screensize", "displaysize", "screensizeinches"],
        ["memory", "ram", "ramsize", "memorysize"],
        ["processor", "cpu", "cputype", "processortype"],
        ["connectivity", "connection", "connectiontype"],
        ["numberofitemsinset", "quantity", "unitquantity", "numberofpieces"],
        ["features", "specialfeatures", "keyfeatures"],
        ["warranty", "manufacturerwarranty"],
        ["compatiblebrand", "compatiblewithbrand"],
        ["voltage", "inputvoltage", "ratedvoltage"],
    ];

    private static readonly Dictionary<string, int> AliasIndex = BuildAliasIndex();

    private static Dictionary<string, int> BuildAliasIndex()
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < AliasGroups.Length; i++)
            foreach (var raw in AliasGroups[i])
            {
                var key = NormalizeName(raw);
                if (key.Length > 0) map.TryAdd(key, i);
            }
        return map;
    }

    // Find the aspect a seller-typed key refers to, or null. Tried in descending order of
    // certainty, and every step after the first insists the answer is unique.
    public static CategoryAspect? MatchAspectName(string? key, IReadOnlyList<CategoryAspect> aspects)
    {
        if (string.IsNullOrWhiteSpace(key) || aspects.Count == 0) return null;

        var norm = NormalizeName(key);
        if (norm.Length == 0) return null;

        // 1. Same name.
        var exact = aspects.Where(a => NormalizeName(a.Name) == norm).ToList();
        if (exact.Count > 0) return exact[0];

        // 2. Same meaning, and only one aspect in this category carries it.
        if (AliasIndex.TryGetValue(norm, out var group))
        {
            var viaAlias = aspects
                .Where(a => AliasIndex.TryGetValue(NormalizeName(a.Name), out var g) && g == group)
                .ToList();
            if (viaAlias.Count == 1) return viaAlias[0];
        }

        // 3. Same words. "GPU Model" ↔ "Chipset/GPU Model" — enough overlap, and unambiguous.
        var keyTokens = Tokens(key);
        if (keyTokens.Count == 0) return null;

        CategoryAspect? best = null;
        var bestScore = 0.0;
        var tied = false;

        foreach (var a in aspects)
        {
            var aTokens = Tokens(a.Name);
            if (aTokens.Count == 0) continue;

            var shared = keyTokens.Intersect(aTokens, StringComparer.Ordinal).Count();
            if (shared == 0) continue;

            // Jaccard, so "Brand" against "Compatible Brand" scores 0.5 and stays below the bar —
            // those genuinely are different fields and conflating them mislabels the item.
            var score = (double)shared / keyTokens.Union(aTokens, StringComparer.Ordinal).Count();

            if (score > bestScore + 0.0001) { bestScore = score; best = a; tied = false; }
            else if (Math.Abs(score - bestScore) <= 0.0001) tied = true;
        }

        return bestScore >= 0.6 && !tied ? best : null;
    }

    // ── Value matching ────────────────────────────────────────────────────────────────────────

    private static string NormalizeValue(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return "";
        return NonAlnum().Replace(v.Trim().ToLowerInvariant(), "");
    }

    // Values that mean the same thing to a person and different things to eBay's validator.
    private static readonly Dictionary<string, string[]> ValueSynonyms = new(StringComparer.Ordinal)
    {
        ["doesnotapply"]  = ["na", "n/a", "none", "nonapplicable", "notapplicable", "-"],
        ["unbranded"]     = ["generic", "unbrandedgeneric", "noname", "nobrand", "unbranded/generic"],
        ["yes"]           = ["true", "y"],
        ["no"]            = ["false", "n"],
        ["notspecified"]  = ["unknown", "unspecified"],
    };

    // Return eBay's own spelling of a value, or null when it isn't one of eBay's values.
    public static string? CanonicalizeValue(string? value, IReadOnlyList<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(value) || allowed.Count == 0) return null;

        var raw = value.Trim();

        foreach (var a in allowed)
            if (string.Equals(a, raw, StringComparison.Ordinal)) return a;

        var norm = NormalizeValue(raw);
        if (norm.Length == 0) return null;

        foreach (var a in allowed)
            if (NormalizeValue(a) == norm) return a;

        // Plural/singular. "Speakers" is "Speaker" as far as a buyer's filter is concerned.
        foreach (var a in allowed)
        {
            var an = NormalizeValue(a);
            if (an + "s" == norm || an == norm + "s") return a;
        }

        foreach (var (canonical, alts) in ValueSynonyms)
        {
            var family = alts.Select(NormalizeValue).Append(canonical).ToHashSet(StringComparer.Ordinal);
            if (!family.Contains(norm)) continue;
            foreach (var a in allowed)
                if (family.Contains(NormalizeValue(a))) return a;
        }

        return null;
    }

    // ── Inference: reading the answer out of the seller's own words ───────────────────────────

    // Find which of eBay's allowed values appears in a piece of the seller's text.
    //
    // Two matches can mean two different things, and the difference decides everything:
    //
    //   REFINEMENT — "S19" and "S19 Pro" both hit on "Antminer S19 Pro". eBay's value lists
    //     overlap by design, and the longer one is the actual model. Taking the shorter would
    //     list a $2,000 machine as a $700 one.
    //
    //   AMBIGUITY — "Red" and "Blue" both hit on "Red and Blue pair". Neither is a refinement of
    //     the other; the app genuinely doesn't know which the item is, and picking the longer
    //     string would be deciding a colour on the strength of its spelling.
    //
    // So: longest wins only when every other match is contained within it. Otherwise, null.
    public static string? FindValueInText(string? text, IReadOnlyList<string> allowed, int minLength = 2)
    {
        if (string.IsNullOrWhiteSpace(text) || allowed.Count == 0) return null;

        var haystack = " " + NonAlnum().Replace(text.ToLowerInvariant(), " ").Trim() + " ";

        var hits = new List<(string Original, string Core)>();
        foreach (var candidate in allowed)
        {
            var core = NonAlnum().Replace(candidate.ToLowerInvariant(), " ").Trim();
            if (core.Length < minLength) continue;

            // Whole-token match only. Without the spaces, "Red" matches "Prepared" and the app
            // confidently reports a colour the seller never mentioned.
            if (!haystack.Contains(" " + core + " ", StringComparison.Ordinal)) continue;

            if (!hits.Any(h => h.Core == core)) hits.Add((candidate, core));
        }

        if (hits.Count == 0) return null;
        if (hits.Count == 1) return hits[0].Original;

        var longest = hits.MaxBy(h => h.Core.Length);
        var allRefineIntoIt = hits.All(h =>
            h.Core == longest.Core ||
            (" " + longest.Core + " ").Contains(" " + h.Core + " ", StringComparison.Ordinal) ||
            longest.Core.StartsWith(h.Core + " ", StringComparison.Ordinal) ||
            longest.Core.EndsWith(" " + h.Core, StringComparison.Ordinal));

        return allRefineIntoIt ? longest.Original : null;
    }

    // Single-word values shorter than this are too collision-prone to pull out of free text —
    // a "Type" list containing "PC" will match the "pc" in "2 pc set".
    private const int MinTextMatchLength = 3;

    public sealed record Suggestion(string Value, string Confidence, string Source);

    // Everything the app knows about the item, gathered once so the per-aspect rules below stay
    // readable. The identity comes from the existing ProductIdentityExtractor — the same
    // brand/model/hashrate parser the sold-comps pipeline runs on, so a listing and its comps are
    // read by one set of rules rather than two.
    public sealed record ListingFacts(
        string Title,
        string DescriptionText,
        string Brand,
        string Mpn,
        string Upc,
        string Ean,
        string Isbn,
        string Condition,
        ProductIdentity? Identity);

    // Aspects nothing in a listing can honestly answer. Guessing "Country/Region of Manufacture"
    // from a brand name is how a seller ends up with a false country of origin on a live listing,
    // which is a legal claim, not a nice-to-have. Left for the seller.
    private static readonly HashSet<string> NeverInfer = new(StringComparer.Ordinal)
    {
        "countryregionofmanufacture",
        "countryoforigin",
        "californiaprop65warning",
        "unitquantity",
        "unittype",
        "itemweight",
        "itemheight",
        "itemwidth",
        "itemlength",
        "manufacturerwarranty",
    };

    // Whether an aspect may be answered by anything other than the seller typing it. Public so the
    // market-derived suggestions in SearchTermMiner obey the same refusal list — a country of
    // origin read off a competitor's title is exactly the legal claim this list exists to refuse,
    // and it would be worse from that source, not better.
    public static bool CanInfer(string? aspectName) => !NeverInfer.Contains(NormalizeName(aspectName));

    public static Suggestion? Suggest(CategoryAspect aspect, ListingFacts facts)
    {
        var norm = NormalizeName(aspect.Name);
        if (NeverInfer.Contains(norm)) return null;

        // 1. Fields the seller already filled in elsewhere on the form. Highest confidence there
        //    is — it isn't inference, it's the same value under eBay's name for it.
        var direct = DirectFieldValue(norm, facts);
        if (!string.IsNullOrWhiteSpace(direct))
        {
            var canonical = aspect.Values.Count > 0 ? CanonicalizeValue(direct, aspect.Values) : direct;

            // A SELECTION_ONLY aspect whose list doesn't contain the seller's own value: offering
            // it would produce a publish failure. Fall through and look for a legal value instead.
            if (canonical != null)
                return new Suggestion(canonical, "high", DirectFieldSource(norm));
            if (!aspect.SelectionOnly)
                return new Suggestion(direct, "high", DirectFieldSource(norm));
        }

        // 2. eBay published the legal values, so look for one of them in the seller's own text.
        if (aspect.Values.Count > 0)
        {
            var fromTitle = FindValueInText(facts.Title, aspect.Values, MinTextMatchLength);
            if (fromTitle != null)
                return new Suggestion(fromTitle, "high", "found in your title");

            var fromDesc = FindValueInText(facts.DescriptionText, aspect.Values, MinTextMatchLength);
            if (fromDesc != null)
                return new Suggestion(fromDesc, "medium", "found in your description");
        }

        // 3. Spec fields the identity extractor already pulls out of titles for the comps engine.
        var fromIdentity = IdentityValue(norm, facts.Identity);
        if (!string.IsNullOrWhiteSpace(fromIdentity) && LooksLikeASpecValue(fromIdentity))
        {
            if (aspect.SelectionOnly)
            {
                var canonical = CanonicalizeValue(fromIdentity, aspect.Values);
                return canonical == null ? null : new Suggestion(canonical, "medium", "read from your title");
            }
            return new Suggestion(fromIdentity, "medium", "read from your title");
        }

        // 4. A required identifier the seller genuinely doesn't have. eBay's own answer for this
        //    is the literal string "Does Not Apply" — a required MPN left blank fails the publish,
        //    while "Does Not Apply" is accepted and is what the item actually is.
        if (aspect.Required && (norm is "mpn" or "upc" or "ean" or "isbn"))
        {
            var noValue = aspect.Values.Count > 0
                ? CanonicalizeValue("Does Not Apply", aspect.Values) ?? "Does Not Apply"
                : "Does Not Apply";
            return new Suggestion(noValue, "medium", "no number for this item");
        }

        return null;
    }

    // The identity extractor's Model is "whatever words are left after every known field has been
    // claimed", which on a real title reads "S19 Pro ASIC with PSU" — leftover prose, not a model
    // number. Caught on a live run against the seller's own inventory: it was being offered at
    // medium confidence, which means "Fill from my listing" would have written it in. A junk Item
    // Specific is worse than a blank one, because a blank one is visibly blank. A genuine spec
    // value is short, so anything that reads like a sentence fragment is refused instead.
    private static bool LooksLikeASpecValue(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length is 0 or > 30) return false;
        return trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 3;
    }

    private static string DirectFieldValue(string normalizedAspect, ListingFacts f) => normalizedAspect switch
    {
        "brand"   => f.Brand,
        "mpn"     => f.Mpn,
        "upc"     => f.Upc,
        "ean"     => f.Ean,
        "isbn"    => f.Isbn,
        _         => "",
    };

    private static string DirectFieldSource(string normalizedAspect) => normalizedAspect switch
    {
        "brand" => "from the Brand field",
        "mpn"   => "from the MPN field",
        "upc"   => "from the UPC field",
        "ean"   => "from the EAN field",
        "isbn"  => "from the ISBN field",
        _       => "from your listing",
    };

    private static string IdentityValue(string normalizedAspect, ProductIdentity? id)
    {
        if (id == null) return "";
        return normalizedAspect switch
        {
            "brand"    => id.Brand ?? id.Manufacturer ?? "",
            "model"    => id.Model ?? "",
            "mpn"      => id.PartNumber ?? "",
            "hashrate" => id.Hashrate ?? "",
            "voltage"  => id.Voltage ?? "",
            "wattage" or "powerconsumption" => id.Wattage ?? "",
            "capacity" or "storagecapacity" => id.Capacity ?? "",
            "series"   => id.Series ?? "",
            _          => "",
        };
    }

    // ── Putting it together ───────────────────────────────────────────────────────────────────

    // Match the seller's typed specifics onto eBay's aspects, judge each one, and offer a value
    // where the app can find it. Returns the fields in eBay's order plus the names the seller used
    // that no aspect claimed.
    public static (List<AspectField> Fields, List<string> Custom) Evaluate(
        IReadOnlyList<CategoryAspect> aspects,
        IReadOnlyDictionary<string, string> sellerSpecifics,
        ListingFacts facts)
    {
        var fields = new List<AspectField>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Resolve every typed key once, so one seller key can't satisfy two aspects.
        var resolved = new Dictionary<string, (string Key, string Value)>(StringComparer.Ordinal);
        foreach (var (key, value) in sellerSpecifics)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            var match = MatchAspectName(key, aspects);
            if (match == null) continue;
            // First key wins; a later duplicate stays a custom specific rather than overwriting.
            if (resolved.TryAdd(match.Name, (key, value ?? ""))) claimed.Add(key);
        }

        foreach (var aspect in aspects)
        {
            var field = new AspectField
            {
                Name          = aspect.Name,
                Required      = aspect.Required,
                Recommended   = aspect.Recommended,
                SelectionOnly = aspect.SelectionOnly,
                MultiSelect   = aspect.MultiSelect,
                MaxLength     = aspect.MaxLength,
                Values        = aspect.Values,
            };

            resolved.TryGetValue(aspect.Name, out var supplied);
            var value = (supplied.Value ?? "").Trim();
            if (!string.IsNullOrEmpty(value) &&
                !string.Equals(supplied.Key, aspect.Name, StringComparison.OrdinalIgnoreCase))
                field.MatchedFromKey = supplied.Key;

            field.Value = value;

            if (!string.IsNullOrEmpty(value))
            {
                // A multi-value specific arrives pipe-separated, the way eBay itself splits them.
                var parts = aspect.MultiSelect
                    ? value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : [value];

                if (aspect.MaxLength > 0 && parts.Any(p => p.Length > aspect.MaxLength))
                {
                    field.State = AspectState.TooLong;
                    field.Note  = $"eBay allows {aspect.MaxLength} characters here.";
                }
                else if (aspect.ValuesAreComplete)
                {
                    var canonical = parts.Select(p => CanonicalizeValue(p, aspect.Values)).ToList();
                    if (canonical.Any(c => c == null))
                    {
                        field.State = AspectState.InvalidValue;
                        field.Note  = "eBay only accepts its own values for this one — pick from the list.";
                        var fix = Suggest(aspect, facts);
                        if (fix != null)
                        {
                            field.SuggestedValue        = fix.Value;
                            field.SuggestionConfidence  = fix.Confidence;
                            field.SuggestionSource      = fix.Source;
                        }
                    }
                    else
                    {
                        field.State = AspectState.Filled;
                        var fixedUp = string.Join("|", canonical);
                        // Silent correction only where it's spelling, not meaning: "bitmain" is
                        // "Bitmain". The seller sees the corrected value in the field.
                        if (!string.Equals(fixedUp, value, StringComparison.Ordinal))
                        {
                            field.Value = fixedUp;
                            field.Note  = "Matched to eBay's spelling.";
                        }
                    }
                }
                else
                {
                    field.State = AspectState.Filled;
                }
            }
            else
            {
                field.State = aspect.Required   ? AspectState.MissingRequired
                            : aspect.Recommended ? AspectState.MissingRecommended
                                                 : AspectState.Optional;

                var suggestion = Suggest(aspect, facts);
                if (suggestion != null)
                {
                    field.SuggestedValue       = suggestion.Value;
                    field.SuggestionConfidence = suggestion.Confidence;
                    field.SuggestionSource     = suggestion.Source;
                }
            }

            fields.Add(field);
        }

        var custom = sellerSpecifics.Keys
            .Where(k => !string.IsNullOrWhiteSpace(k) && !claimed.Contains(k))
            .ToList();

        return (fields, custom);
    }

    // The values "Fill from my listing" would write. Low-confidence guesses are left on the field
    // as an offer instead — a one-click button that writes something uncertain into a live
    // listing is a button that makes the seller's data worse.
    public static Dictionary<string, string> AutoFillable(IEnumerable<AspectField> fields)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in fields)
        {
            if (string.IsNullOrWhiteSpace(f.SuggestedValue)) continue;
            if (f.SuggestionConfidence is not ("high" or "medium")) continue;
            // Never overwrite a value the seller supplied — except one eBay will reject outright.
            if (!string.IsNullOrWhiteSpace(f.Value) && f.State != AspectState.InvalidValue) continue;
            result[f.Name] = f.SuggestedValue;
        }
        return result;
    }

    // ── Plain text out of the description ─────────────────────────────────────────────────────

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTag();

    // Descriptions in this app are HTML. Searching raw markup for value matches would find
    // "Table" in "<table>" and colours in inline styles.
    public static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var text = HtmlTag().Replace(html, " ");
        var sb = new StringBuilder(text.Length);
        foreach (var part in text.Split('&'))
        {
            var semi = part.IndexOf(';');
            if (sb.Length == 0) { sb.Append(part); continue; }
            if (semi is > 0 and <= 8)
            {
                var entity = part[..semi].ToLowerInvariant();
                sb.Append(entity switch
                {
                    "amp" => "&", "lt" => "<", "gt" => ">", "quot" => "\"",
                    "#39" or "apos" => "'", "nbsp" => " ", _ => " ",
                });
                sb.Append(part[(semi + 1)..]);
            }
            else sb.Append(' ').Append(part);
        }
        return string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
