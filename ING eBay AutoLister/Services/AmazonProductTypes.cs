using System.Text.Json;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

// ── Reading Amazon's product type definitions ─────────────────────────────────────────────────
//
// Amazon will not accept a free-form listing. Every product belongs to a PRODUCT TYPE, and the
// product type carries a JSON Schema saying exactly which attributes exist, which are required,
// what type each one is and — where Amazon has decided — the closed list of values it will accept.
// This file is the pure half of talking to that API: pick the product type, read the schema, and
// say which attributes a seller has to fill in.
//
// It is separated from AmazonProductTypeService for the reason ParseAspects is separated from
// GetCategoryAspectsAsync on the eBay side: the interesting part is what the response MEANS, and
// that has to be testable without a token, a network or a seller account. Which matters more here
// than it did there — this app currently has no way to obtain an Amazon token at all, so the
// parser is the only part that can be proven at all this side of a credential.
//
// The three jobs, all independently wrong-able:
//
//   1. CHOOSING. The seller typed "bluetooth speaker"; Amazon offers SPEAKERS, PORTABLE_SPEAKER
//      and BLUETOOTH_SPEAKER. Picking the wrong one produces a schema whose required attributes
//      are the wrong ones, which is worse than no answer — the seller fills in nine fields and is
//      still rejected.
//
//   2. UNWRAPPING. Every attribute in the schema is literally an array of objects, because a value
//      can differ per marketplace and per language. What the seller types is the string inside.
//      A form built on the literal schema asks for an array; a form built on this asks for a title.
//
//   3. SEPARATING REQUIRED FROM OPTIONAL. A product type has on the order of 150 attributes and
//      fewer than a dozen that decide acceptance. Amazon also has a third state — required unless
//      a sibling is supplied instead — and flattening that into either of the other two is wrong
//      in a way the seller only discovers on submission.

/// <summary>
/// The Product Type Definitions API: which paths this app calls, and with what.
/// </summary>
/// <remarks>
/// Paths only, never hosts. The host comes from <see cref="AmazonEndpoints"/> via
/// <see cref="AmazonService.SendAsync"/>, so nothing here can address production while the app
/// reports sandbox — the same rule the auth layer follows.
/// </remarks>
public static class AmazonDefinitionsApi
{
    /// <summary>The API version this app is written against.</summary>
    public const string Version = "2020-09-01";

    private const string Root = $"/definitions/{Version}/productTypes";

    /// <summary>Everything a listing needs — the product half and the offer half.</summary>
    public const string RequirementsListing = "LISTING";

    /// <summary>Product data only, for an offer on someone else's existing ASIN.</summary>
    public const string RequirementsProductOnly = "LISTING_PRODUCT_ONLY";

    /// <summary>Price and availability only.</summary>
    public const string RequirementsOfferOnly = "LISTING_OFFER_ONLY";

    /// <summary>Ask for whatever Amazon currently publishes, rather than pinning a version.</summary>
    public const string LatestVersion = "LATEST";

    public const string DefaultLocale = "en_US";

    /// <summary>
    /// Search product types for a seller's words.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>keywords</c> is a COMMA-delimited list, not a phrase, so "bluetooth speaker" is sent as
    /// <c>bluetooth,speaker</c>. Sent as one keyword containing a space it matches nothing, which
    /// reads exactly like Amazon having no product type for the item.
    /// </para>
    /// <para>
    /// Amazon also accepts <c>itemName</c> — a whole product title, matched the way its own
    /// classifier does — but the two are mutually exclusive and <c>itemName</c> answers a different
    /// question ("what is this specific product?"). This app is searching, so it sends keywords.
    /// </para>
    /// </remarks>
    public static string SearchPath(string query, string marketplaceId, string locale = DefaultLocale)
    {
        var keywords = string.Join(',', Keywords(query));
        var path = $"{Root}?marketplaceIds={Uri.EscapeDataString(marketplaceId ?? "")}";
        if (keywords.Length > 0) path += $"&keywords={Uri.EscapeDataString(keywords)}";
        if (!string.IsNullOrWhiteSpace(locale)) path += $"&locale={Uri.EscapeDataString(locale)}";
        return path;
    }

    /// <summary>The words of a query, in order, with duplicates and punctuation dropped.</summary>
    public static List<string> Keywords(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var words = new List<string>();
        foreach (var raw in query.Split([' ', ',', '/', '-', '_', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var word = new string(raw.Where(char.IsLetterOrDigit).ToArray());
            if (word.Length == 0) continue;
            if (seen.Add(word)) words.Add(word);
        }
        return words;
    }

    /// <summary>One product type's definition, which carries the link to the schema itself.</summary>
    public static string DefinitionPath(
        string productType,
        string marketplaceId,
        string requirements = RequirementsListing,
        string locale = DefaultLocale,
        string productTypeVersion = LatestVersion) =>
        $"{Root}/{Uri.EscapeDataString(productType ?? "")}" +
        $"?marketplaceIds={Uri.EscapeDataString(marketplaceId ?? "")}" +
        $"&productTypeVersion={Uri.EscapeDataString(productTypeVersion)}" +
        $"&requirements={Uri.EscapeDataString(requirements)}" +
        $"&locale={Uri.EscapeDataString(locale)}";
}

// ── Reading the search response ───────────────────────────────────────────────────────────────

/// <summary>Reads <c>searchDefinitionsProductTypes</c>. Shape only; the choosing is next door.</summary>
public static class AmazonProductTypeSearchResponse
{
    /// <summary>
    /// The product types in Amazon's answer, in Amazon's order.
    /// </summary>
    /// <remarks>
    /// A malformed or unexpected body yields an empty list rather than an exception: the caller's
    /// next move for "Amazon returned nothing usable" and "Amazon returned no product types" is the
    /// same sentence to the seller, and a parse failure here must not take down a lookup.
    /// </remarks>
    public static List<AmazonProductType> Parse(string? json)
    {
        var results = new List<AmazonProductType>();
        if (string.IsNullOrWhiteSpace(json)) return results;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return results;
            if (!doc.RootElement.TryGetProperty("productTypes", out var arr) ||
                arr.ValueKind != JsonValueKind.Array) return results;

            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;

                var name = Str(el, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;

                var marketplaces = new List<string>();
                if (el.TryGetProperty("marketplaceIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
                    foreach (var id in ids.EnumerateArray())
                        if (id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } s)
                            marketplaces.Add(s);

                results.Add(new AmazonProductType(name, Str(el, "displayName"), marketplaces));
            }
        }
        catch (JsonException) { /* Nothing usable. An empty list is the honest answer. */ }

        return results;
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

// ── Choosing one ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Picks the product type a seller's words mean, or refuses.
/// </summary>
/// <remarks>
/// <para>
/// The same ethic as <see cref="AspectMatcher"/>: it refuses rather than guesses, and it says how
/// sure it is and why. A wrong product type is not a smaller version of no product type — the
/// seller fills in that schema's required attributes, submits, and Amazon rejects the listing for
/// missing attributes belonging to a schema they were never shown.
/// </para>
/// <para>
/// Amazon's own search decides the candidates; this only decides between them, and only on the
/// words. Nothing here reaches for a catalogue, a title classifier or a model — those all exist,
/// and every one of them can be wrong quietly, which is the failure this refuses to have.
/// </para>
/// </remarks>
public static class AmazonProductTypeChooser
{
    /// <summary>Two candidates this close are not distinguishable by their names, so neither is chosen.</summary>
    public const double AmbiguityMargin = 0.05;

    /// <summary>Below this, a win on shared words is not worth calling a match.</summary>
    public const double MinimumScore = 0.20;

    /// <summary>
    /// The best product type for <paramref name="query"/>, or null when the words do not decide it.
    /// </summary>
    public static AmazonProductTypeChoice? Choose(string? query, IReadOnlyList<AmazonProductType> candidates)
    {
        if (candidates.Count == 0) return null;

        var queryTokens = Normalize(query);

        // Amazon offered exactly one. It ran its own match to get there, so this is its answer
        // rather than a choice between alternatives — taken, and labelled as what it is.
        if (candidates.Count == 1 && queryTokens.Count == 0)
            return new AmazonProductTypeChoice(candidates[0], 0,
                "low", $"Amazon's only match. Nothing in the search words distinguishes it.");

        var scored = candidates
            .Select(c => (Type: c, Score: Score(queryTokens, c), Size: Normalize(c.Name).Count))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Size)                                  // the more specific of two equals
            .ThenBy(x => x.Type.Name, StringComparer.Ordinal)     // and then something deterministic
            .ToList();

        var best   = scored[0];
        var runner = scored.Count > 1 ? scored[1] : default;

        if (best.Score <= 0)
            return candidates.Count == 1
                ? new AmazonProductTypeChoice(candidates[0], 0, "low",
                    "Amazon's only match. None of the search words appear in its name.")
                : null;

        if (best.Score < MinimumScore) return null;

        // Two names the words cannot tell apart. Refusing here is what stops "speaker" from
        // silently becoming HOME_THEATER_SYSTEM because it sorted first.
        if (scored.Count > 1 && best.Score - runner.Score < AmbiguityMargin) return null;

        var exact = Math.Abs(best.Score - 1.0) < 0.0001;
        var confidence = exact ? "high"
            : best.Score >= 0.5 ? "medium"
            : "low";

        var why = exact
            ? $"\"{query}\" is exactly Amazon's {best.Type.Label}."
            : $"Closest of {candidates.Count} product type{(candidates.Count == 1 ? "" : "s")} Amazon offered " +
              $"for \"{query}\"{(scored.Count > 1 ? $", ahead of {runner.Type.Label}" : "")}.";

        return new AmazonProductTypeChoice(best.Type, best.Score, confidence, why);
    }

    /// <summary>
    /// How well one candidate's words match the query's, 0 to 1.
    /// </summary>
    /// <remarks>
    /// Jaccard over singularised tokens, scored against the identifier and the display name and
    /// taking the better — <c>BLUETOOTH_SPEAKER</c> and "Bluetooth Speakers" are the same product
    /// type under two spellings, and a seller's "speaker" has to reach both.
    /// </remarks>
    public static double Score(IReadOnlyList<string> queryTokens, AmazonProductType candidate)
    {
        if (queryTokens.Count == 0) return 0;
        return Math.Max(
            Overlap(queryTokens, Normalize(candidate.Name)),
            Overlap(queryTokens, Normalize(candidate.DisplayName)));
    }

    private static double Overlap(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var setA = new HashSet<string>(a, StringComparer.Ordinal);
        var setB = new HashSet<string>(b, StringComparer.Ordinal);
        var shared = setA.Count(t => setB.Contains(t));
        if (shared == 0) return 0;
        return (double)shared / setA.Union(setB, StringComparer.Ordinal).Count();
    }

    /// <summary>
    /// A name or a query as comparable tokens: lowercase, punctuation gone, plurals folded.
    /// </summary>
    /// <remarks>
    /// The plural fold is what makes "speaker" find <c>SPEAKERS</c>, which is how Amazon actually
    /// names most of its product types. Only a trailing "s" on a word long enough for it to be a
    /// plural rather than the word — "gas" and "lens" are not "ga" and "len".
    /// </remarks>
    public static List<string> Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var tokens = new List<string>();
        foreach (var raw in text.Split([' ', '_', '-', '/', ',', '.', '&', '(', ')', '\t'],
                                       StringSplitOptions.RemoveEmptyEntries))
        {
            var word = new string(raw.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (word.Length == 0) continue;
            if (word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss")) word = word[..^1];
            if (!tokens.Contains(word, StringComparer.Ordinal)) tokens.Add(word);
        }
        return tokens;
    }
}

// ── Reading the definition response ───────────────────────────────────────────────────────────

/// <summary>
/// Reads <c>getDefinitionsProductType</c> — the metadata and, crucially, the link to the schema.
/// </summary>
/// <remarks>
/// The definition response does not contain the schema. It contains a short-lived pre-signed URL to
/// it, plus a checksum and a version. That indirection is the reason this app can cache schemas at
/// all: the definition call is small and cheap, so it can be made every time to learn whether the
/// large document on disk is still the current one.
/// </remarks>
public static class AmazonDefinitionResponse
{
    /// <summary>
    /// The metadata, with <see cref="AmazonProductTypeDefinition.Attributes"/> left empty — the
    /// schema at <see cref="AmazonProductTypeDefinition.SchemaUrl"/> has not been fetched yet.
    /// </summary>
    public static AmazonProductTypeDefinition Parse(string? json)
    {
        var result = new AmazonProductTypeDefinition();
        if (string.IsNullOrWhiteSpace(json))
        {
            result.Status = AmazonDefinitionStatus.Error;
            result.Message = "Amazon returned an empty product type definition.";
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                result.Status = AmazonDefinitionStatus.Error;
                result.Message = "Amazon's product type definition was not a JSON object.";
                return result;
            }

            result.ProductType         = Str(root, "productType");
            result.DisplayName         = Str(root, "displayName");
            result.Locale              = Str(root, "locale");
            result.Requirements        = Str(root, "requirements");
            result.RequirementsEnforced = Str(root, "requirementsEnforced");

            if (root.TryGetProperty("marketplaceIds", out var ids) &&
                ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() > 0 &&
                ids[0].ValueKind == JsonValueKind.String)
                result.MarketplaceId = ids[0].GetString() ?? "";

            if (root.TryGetProperty("productTypeVersion", out var version) &&
                version.ValueKind == JsonValueKind.Object)
                result.Version = Str(version, "version");

            if (root.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.Object)
            {
                result.SchemaChecksum = Str(schema, "checksum");
                if (schema.TryGetProperty("link", out var link) && link.ValueKind == JsonValueKind.Object)
                    result.SchemaUrl = Str(link, "resource");
            }

            if (string.IsNullOrWhiteSpace(result.SchemaUrl))
            {
                result.Status = AmazonDefinitionStatus.Error;
                result.Message = "Amazon's product type definition carried no link to the schema, so there is " +
                                 "nothing to read the attributes out of.";
            }
        }
        catch (JsonException)
        {
            result.Status = AmazonDefinitionStatus.Error;
            result.Message = "Amazon's product type definition could not be read as JSON.";
        }

        return result;
    }

    /// <summary>
    /// Amazon's property groups: which named group each attribute belongs to. Returned flattened —
    /// <c>item_name</c> → "Product Identity" — because that is how an attribute list uses it.
    /// </summary>
    public static Dictionary<string, string> ParseGroups(string? json)
    {
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return groups;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return groups;
            if (!doc.RootElement.TryGetProperty("propertyGroups", out var pg) ||
                pg.ValueKind != JsonValueKind.Object) return groups;

            foreach (var group in pg.EnumerateObject())
            {
                if (group.Value.ValueKind != JsonValueKind.Object) continue;
                var title = Str(group.Value, "title");
                if (string.IsNullOrWhiteSpace(title)) title = AmazonProductType.Humanize(group.Name);

                if (!group.Value.TryGetProperty("propertyNames", out var names) ||
                    names.ValueKind != JsonValueKind.Array) continue;

                foreach (var n in names.EnumerateArray())
                    if (n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } prop)
                        groups[prop] = title;
            }
        }
        catch (JsonException) { /* No groups is a cosmetic loss, not a failure. */ }

        return groups;
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

// ── Reading the schema itself ─────────────────────────────────────────────────────────────────

/// <summary>
/// Turns one Amazon product type JSON Schema into a flat, ordered list of attributes.
/// </summary>
/// <remarks>
/// <para>
/// Amazon's schemas extend JSON Schema 2019-09 with their own vocabulary — <c>editable</c>,
/// <c>hidden</c>, <c>enumNames</c>, <c>selectors</c>, <c>maxUniqueItems</c>,
/// <c>maxUtf8ByteLength</c> — and this reads the ones that change what a seller is asked for. It
/// is NOT a validator: nothing here decides whether a listing is acceptable, only what Amazon
/// says it wants. Amazon validates, and it is the only thing that can.
/// </para>
/// <para>
/// Everything is bounded. <see cref="MaxDepth"/> caps the recursion, so a schema with a cyclic
/// <c>$ref</c> — which a hostile or simply broken document can have — terminates instead of
/// filling the stack, and a <c>$ref</c> that cannot be resolved leaves the attribute typed as
/// unspecified rather than throwing away the whole schema.
/// </para>
/// </remarks>
public static class AmazonSchemaParser
{
    /// <summary>How far into nested objects the parser will read. Deep enough for a scheduled price.</summary>
    public const int MaxDepth = 5;

    /// <summary>
    /// The properties that pick WHICH value, not what the value is.
    /// </summary>
    /// <remarks>
    /// A schema usually names these itself in <c>selectors</c> and that is what is used when it
    /// does; this is the fallback. They are stripped because they are the envelope: a seller types
    /// an item name, and the marketplace and language are decided by which marketplace the listing
    /// is going to. Left in, every single-value attribute would present as a three-field form.
    /// </remarks>
    public static readonly string[] DefaultSelectors = ["marketplace_id", "language_tag"];

    /// <summary>
    /// Reads a schema document into attributes, required ones first.
    /// </summary>
    /// <param name="schemaJson">The schema document fetched from the definition's schema link.</param>
    /// <param name="groups">Property name → group title, from <see cref="AmazonDefinitionResponse.ParseGroups"/>.</param>
    public static List<AmazonAttribute> Parse(string? schemaJson, IReadOnlyDictionary<string, string>? groups = null)
    {
        var attributes = new List<AmazonAttribute>();
        if (string.IsNullOrWhiteSpace(schemaJson)) return attributes;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(schemaJson); }
        catch (JsonException) { return attributes; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return attributes;
            if (!root.TryGetProperty("properties", out var properties) ||
                properties.ValueKind != JsonValueKind.Object) return attributes;

            var required    = RequiredNames(root);
            var conditional = ConditionalRequirements(root);

            foreach (var property in properties.EnumerateObject())
            {
                var attribute = ReadProperty(property.Name, property.Value, root, depth: 0);

                attribute.Required = required.Contains(property.Name);

                if (!attribute.Required && conditional.TryGetValue(property.Name, out var alternatives))
                {
                    attribute.ConditionallyRequired = true;
                    attribute.RequirementNote = alternatives.Count == 0
                        ? "Required by one of Amazon's alternative requirement sets."
                        : "Required unless you supply " + Join(alternatives) + " instead.";
                }

                if (groups is not null && groups.TryGetValue(property.Name, out var group))
                    attribute.Group = group;

                attributes.Add(attribute);
            }
        }

        // Required first, then conditionally required, then everything else — each bucket keeping
        // Amazon's own property order. A form that lists 150 attributes alphabetically buries the
        // nine that decide whether the listing is accepted.
        return attributes
            .Select((a, index) => (a, index))
            .OrderBy(x => x.a.Required ? 0 : x.a.ConditionallyRequired ? 1 : 2)
            .ThenBy(x => x.index)
            .Select(x => x.a)
            .ToList();
    }

    // ── One property ──────────────────────────────────────────────────────────

    private static AmazonAttribute ReadProperty(string name, JsonElement node, JsonElement root, int depth)
    {
        node = Resolve(node, root, depth);

        var attribute = new AmazonAttribute
        {
            Name        = name,
            Title       = Str(node, "title") is { Length: > 0 } t ? t : AmazonProductType.Humanize(name),
            Description = Str(node, "description"),
            Editable    = Bool(node, "editable", defaultValue: true),
            Hidden      = Bool(node, "hidden", defaultValue: false),
            Examples    = Strings(node, "examples"),
            RawType     = TypeOf(node),
        };

        var value = node;

        // The array envelope. Amazon wraps nearly every attribute in one so the same attribute can
        // hold a different value per marketplace and per language.
        if (attribute.RawType == "array" && node.TryGetProperty("items", out var items))
        {
            attribute.MultiSelect = MultipleAllowed(node);
            value = Resolve(items, root, depth);
        }

        // The object envelope inside it. Strip the selectors and see what is left: one property
        // called "value" is a single value wearing a coat; anything else is a genuine composite.
        if (TypeOf(value) == "object" && value.TryGetProperty("properties", out var inner) &&
            inner.ValueKind == JsonValueKind.Object)
        {
            var selectors = SelectorsOf(node, value);
            var content = inner.EnumerateObject()
                .Where(p => !selectors.Contains(p.Name, StringComparer.Ordinal))
                .ToList();

            if (content.Count == 1 && content[0].Name == "value")
            {
                value = Resolve(content[0].Value, root, depth);
            }
            else if (content.Count > 0 && depth < MaxDepth)
            {
                attribute.Type = "object";
                var innerRequired = RequiredNames(value);

                foreach (var child in content)
                {
                    var childAttribute = ReadProperty(child.Name, child.Value, root, depth + 1);
                    childAttribute.Required = innerRequired.Contains(child.Name);
                    attribute.Children.Add(childAttribute);
                }

                // Required children first, same reasoning as the top level.
                attribute.Children = attribute.Children
                    .Select((c, index) => (c, index))
                    .OrderBy(x => x.c.Required ? 0 : 1)
                    .ThenBy(x => x.index)
                    .Select(x => x.c)
                    .ToList();

                return attribute;
            }
            else
            {
                attribute.Type = "object";
                return attribute;
            }
        }

        FillScalar(attribute, value, root, depth);
        return attribute;
    }

    /// <summary>The type, the closed value list and the length limit of a single value.</summary>
    private static void FillScalar(AmazonAttribute attribute, JsonElement value, JsonElement root, int depth)
    {
        attribute.Type = TypeOf(value);

        // Some attributes state their type through a branch rather than directly. Take the first
        // branch that names one, and union the enums — a value legal in any branch is legal.
        if (string.IsNullOrEmpty(attribute.Type))
            foreach (var key in (string[])["anyOf", "oneOf", "allOf"])
            {
                if (!value.TryGetProperty(key, out var branches) || branches.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var raw in branches.EnumerateArray())
                {
                    var branch = Resolve(raw, root, depth);
                    if (string.IsNullOrEmpty(attribute.Type)) attribute.Type = TypeOf(branch);
                    AddEnum(attribute, branch);
                }
            }

        AddEnum(attribute, value);

        // maxUtf8ByteLength is Amazon's own, and for anything outside ASCII it is the binding one.
        // Reported as the smaller of the two so a limit is never overstated.
        var maxLength = Int(value, "maxLength");
        var maxBytes  = Int(value, "maxUtf8ByteLength");
        attribute.MaxLength = maxLength > 0 && maxBytes > 0 ? Math.Min(maxLength, maxBytes)
            : maxLength > 0 ? maxLength
            : maxBytes;

        if (attribute.Examples.Count == 0) attribute.Examples = Strings(value, "examples");
        if (string.IsNullOrWhiteSpace(attribute.Description)) attribute.Description = Str(value, "description");
    }

    private static void AddEnum(AmazonAttribute attribute, JsonElement value)
    {
        if (!value.TryGetProperty("enum", out var values) || values.ValueKind != JsonValueKind.Array) return;

        foreach (var v in values.EnumerateArray())
        {
            var text = v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.True   => "true",
                JsonValueKind.False  => "false",
                _                    => "",
            };
            if (text.Length > 0 && !attribute.Values.Contains(text, StringComparer.Ordinal))
                attribute.Values.Add(text);
        }

        if (attribute.Values.Count > 0) attribute.SelectionOnly = true;

        // enumNames is Amazon's display label for each enum value, in the same order. Only taken
        // when the counts agree — a mismatched pair would label every value with its neighbour's
        // name, and a wrong label on a closed list is worse than no label.
        var labels = Strings(value, "enumNames");
        if (labels.Count > 0 && labels.Count == values.GetArrayLength() && attribute.ValueLabels.Count == 0)
            attribute.ValueLabels = labels;
    }

    // ── Requirements ──────────────────────────────────────────────────────────

    /// <summary>The names in a <c>required</c> array.</summary>
    public static HashSet<string> RequiredNames(JsonElement node)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (node.ValueKind != JsonValueKind.Object) return names;
        if (!node.TryGetProperty("required", out var arr) || arr.ValueKind != JsonValueKind.Array) return names;

        foreach (var n in arr.EnumerateArray())
            if (n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } s)
                names.Add(s);

        return names;
    }

    /// <summary>
    /// Attributes that are required only through an alternative — and which alternatives satisfy
    /// the same rule instead.
    /// </summary>
    /// <remarks>
    /// Amazon expresses "a product identifier OR an ASIN" as a root-level <c>anyOf</c> whose
    /// branches each carry their own <c>required</c>. That is a third state, and neither of the
    /// other two describes it: called required, a seller with an ASIN is sent to find a UPC they
    /// are exempt from; called optional, a seller with neither submits and is rejected.
    /// </remarks>
    public static Dictionary<string, List<string>> ConditionalRequirements(JsonElement root)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object) return result;

        foreach (var key in (string[])["anyOf", "oneOf"])
        {
            if (!root.TryGetProperty(key, out var branches) || branches.ValueKind != JsonValueKind.Array)
                continue;

            var perBranch = branches.EnumerateArray()
                .Select(RequiredNames)
                .Where(set => set.Count > 0)
                .ToList();

            // One branch is not an alternative to anything — that is a plain requirement written
            // the long way, and treating it as conditional would tell a seller they had a choice.
            if (perBranch.Count < 2) continue;

            foreach (var set in perBranch)
                foreach (var name in set)
                {
                    if (!result.TryGetValue(name, out var alternatives))
                        result[name] = alternatives = [];

                    foreach (var other in perBranch)
                    {
                        if (ReferenceEquals(other, set)) continue;
                        foreach (var candidate in other)
                            if (candidate != name && !alternatives.Contains(candidate, StringComparer.Ordinal))
                                alternatives.Add(candidate);
                    }
                }
        }

        return result;
    }

    // ── Schema plumbing ───────────────────────────────────────────────────────

    /// <summary>
    /// Follows a local <c>$ref</c> to what it points at, or returns the node unchanged.
    /// </summary>
    /// <remarks>
    /// Local pointers only — <c>#/$defs/currency</c> and its <c>#/definitions/</c> spelling. A ref
    /// to another document is not followed: that would be this app fetching a URL named inside a
    /// response, which is a class of thing worth never doing on the strength of a remote document.
    /// An unresolvable ref leaves the attribute typed as unspecified, which is honest.
    /// </remarks>
    public static JsonElement Resolve(JsonElement node, JsonElement root, int depth)
    {
        var hops = 0;
        while (node.ValueKind == JsonValueKind.Object &&
               node.TryGetProperty("$ref", out var refElement) &&
               refElement.ValueKind == JsonValueKind.String &&
               hops++ < MaxDepth && depth < MaxDepth)
        {
            var pointer = refElement.GetString() ?? "";
            if (!pointer.StartsWith("#/", StringComparison.Ordinal)) return node;

            var target = root;
            var resolved = true;

            foreach (var rawSegment in pointer[2..].Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                // RFC 6901 escaping. "~1" is a slash and "~0" a tilde, and the order matters.
                var segment = rawSegment.Replace("~1", "/").Replace("~0", "~");
                if (target.ValueKind != JsonValueKind.Object ||
                    !target.TryGetProperty(segment, out var next))
                {
                    resolved = false;
                    break;
                }
                target = next;
            }

            if (!resolved) return node;
            node = target;
        }

        return node;
    }

    /// <summary>The property names that select which value, rather than being part of it.</summary>
    private static string[] SelectorsOf(JsonElement outer, JsonElement inner)
    {
        foreach (var candidate in (JsonElement[])[outer, inner])
        {
            if (candidate.ValueKind != JsonValueKind.Object) continue;
            if (!candidate.TryGetProperty("selectors", out var selectors) ||
                selectors.ValueKind != JsonValueKind.Array) continue;

            var names = selectors.EnumerateArray()
                .Where(s => s.ValueKind == JsonValueKind.String)
                .Select(s => s.GetString() ?? "")
                .Where(s => s.Length > 0)
                .ToArray();

            if (names.Length > 0) return names;
        }

        return DefaultSelectors;
    }

    /// <summary>Whether more than one value may be supplied for an array attribute.</summary>
    /// <remarks>
    /// Amazon caps this two ways: <c>maxItems</c> counts entries, <c>maxUniqueItems</c> counts them
    /// after the selectors are taken into account — an attribute with <c>maxItems: 4</c> and
    /// <c>maxUniqueItems: 1</c> allows one value said four ways (per marketplace, per language),
    /// not four values. So the unique cap wins where Amazon set it.
    /// </remarks>
    private static bool MultipleAllowed(JsonElement node)
    {
        var unique = Int(node, "maxUniqueItems");
        if (unique > 0) return unique > 1;

        var max = Int(node, "maxItems");
        return max == 0 || max > 1;
    }

    private static string TypeOf(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object) return "";
        if (!node.TryGetProperty("type", out var type)) return "";

        if (type.ValueKind == JsonValueKind.String) return type.GetString() ?? "";

        // A union type. "null" is the absence of a value rather than a kind of one.
        if (type.ValueKind == JsonValueKind.Array)
            foreach (var t in type.EnumerateArray())
                if (t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } s && s != "null")
                    return s;

        return "";
    }

    private static string Join(List<string> names) =>
        names.Count switch
        {
            0 => "something else",
            1 => names[0],
            2 => $"{names[0]} or {names[1]}",
            _ => string.Join(", ", names.Take(names.Count - 1)) + " or " + names[^1],
        };

    private static string Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement el, string name, bool defaultValue)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return defaultValue;
        return v.ValueKind switch
        {
            JsonValueKind.True  => true,
            JsonValueKind.False => false,
            _                   => defaultValue,
        };
    }

    private static int Int(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && i > 0 ? i : 0;

    private static List<string> Strings(JsonElement el, string name)
    {
        var results = new List<string>();
        if (el.ValueKind != JsonValueKind.Object) return results;
        if (!el.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return results;

        foreach (var v in arr.EnumerateArray())
        {
            var text = v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.GetRawText(),
                _                    => "",
            };
            if (text.Length > 0) results.Add(text);
        }
        return results;
    }
}

// ── What the sandbox is actually answering ────────────────────────────────────────────────────

/// <summary>
/// Says when an answer came from the sandbox rather than from Amazon's real classification.
/// </summary>
/// <remarks>
/// <para>
/// The SP-API sandbox does not run the Product Type Definitions API — it replays STATIC responses.
/// <c>sandbox.sellingpartnerapi-*.amazon.com/definitions/2020-09-01/productTypes</c> answers with a
/// fixed product type set whatever the <c>keywords</c> say, so a search for a speaker comes back
/// with luggage and a 200 OK.
/// </para>
/// <para>
/// This exists because that failure is silent and looks exactly like success. Every layer above
/// works: the request is well-formed, the response parses, a product type is chosen, its schema is
/// real and its required attributes are real — they are just the required attributes of a piece of
/// luggage. A seller shown that would fill in nine fields for the wrong product. So the sandbox
/// says so, in the answer, every time.
/// </para>
/// </remarks>
public static class AmazonSandboxNotice
{
    /// <summary>The sandbox's fixed answer, named so the notice can point at it when it appears.</summary>
    public const string StaticProductType = "LUGGAGE";

    /// <summary>A sentence about the sandbox, or empty when this is production.</summary>
    public static string For(string? query, IReadOnlyList<AmazonProductType> candidates, bool sandbox)
    {
        if (!sandbox) return "";

        const string Preamble =
            "This is the SP-API sandbox, which replays static product type data rather than searching: " +
            "it returns a fixed set whatever the keywords say.";

        if (candidates.Count == 0)
            return Preamble + " Nothing came back for these words, which says nothing about whether Amazon has " +
                   "a product type for them. Only production can answer that.";

        var queryTokens = AmazonProductTypeChooser.Normalize(query);
        var matched = candidates.Any(c => AmazonProductTypeChooser.Score(queryTokens, c) > 0);

        if (!matched)
            return Preamble + $" What came back ({string.Join(", ", candidates.Take(3).Select(c => c.Name))}) shares " +
                   $"no word with \"{query}\" — this is the canned response, not a classification of the query. " +
                   "Run it against production for a real answer.";

        return Preamble + " These product types happen to match the query, but the answer is still canned — " +
               "treat it as proof the calls work, not as Amazon's classification of this product.";
    }
}

// ── Saying it out loud ────────────────────────────────────────────────────────────────────────

/// <summary>
/// Renders a product type definition as plain text.
/// </summary>
/// <remarks>
/// Exists so the answer can be READ — by an operator against a live deployment, by a test, and by
/// whoever is deciding whether this phase actually works. Required, conditionally required and
/// optional are three visibly different sections, because the whole point of the exercise is that
/// they are three different things.
/// </remarks>
public static class AmazonAttributeReport
{
    public static string Describe(AmazonProductTypeDefinition definition, int optionalToList = 0)
    {
        var lines = new List<string>
        {
            $"Product type : {definition.ProductType}" +
                (string.IsNullOrWhiteSpace(definition.DisplayName) ? "" : $"  ({definition.DisplayName})"),
            $"Marketplace  : {definition.MarketplaceId}   Locale: {definition.Locale}",
            $"Requirements : {definition.Requirements}" +
                (string.IsNullOrWhiteSpace(definition.RequirementsEnforced) ? "" : $" ({definition.RequirementsEnforced})"),
            $"Version      : {definition.Version}",
            "",
        };

        var required      = definition.RequiredAttributes.ToList();
        var conditional   = definition.ConditionallyRequiredAttributes.ToList();
        var optionalCount = definition.OptionalAttributes.Count();

        lines.Add($"REQUIRED ATTRIBUTES ({required.Count})");
        lines.Add(new string('-', 72));
        if (required.Count == 0) lines.Add("  (none)");
        foreach (var attribute in required) lines.AddRange(Lines(attribute));

        if (conditional.Count > 0)
        {
            lines.Add("");
            lines.Add($"CONDITIONALLY REQUIRED ({conditional.Count}) — needed unless the alternative is supplied");
            lines.Add(new string('-', 72));
            foreach (var attribute in conditional) lines.AddRange(Lines(attribute));
        }

        lines.Add("");
        lines.Add($"OPTIONAL ATTRIBUTES: {optionalCount}");
        if (optionalToList > 0)
            foreach (var attribute in definition.OptionalAttributes.Take(optionalToList))
                lines.AddRange(Lines(attribute));

        return string.Join(Environment.NewLine, lines);
    }

    private static IEnumerable<string> Lines(AmazonAttribute attribute)
    {
        yield return $"  {attribute.Name,-42} {attribute.TypeDescription}";

        if (!string.IsNullOrWhiteSpace(attribute.RequirementNote))
            yield return $"      {attribute.RequirementNote}";

        if (attribute.SelectionOnly && attribute.Values.Count > 0)
            yield return $"      values: {string.Join(", ", attribute.Values.Take(8))}" +
                         (attribute.Values.Count > 8 ? $" … (+{attribute.Values.Count - 8})" : "");

        foreach (var child in attribute.Children)
            yield return $"      • {child.Name,-36} {child.TypeDescription}{(child.Required ? "  [required]" : "")}";
    }
}
