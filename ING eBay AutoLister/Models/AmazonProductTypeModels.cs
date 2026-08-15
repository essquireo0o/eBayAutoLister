namespace ING_eBay_AutoLister.Models;

// ── Amazon's answer to "what does this product need?" ─────────────────────────────────────────
//
// The counterpart of ListingReadinessModels.cs, and deliberately shaped to be read next to it —
// but the two marketplaces answer a different question, and the difference is the whole reason
// this file exists rather than reusing CategoryAspect.
//
// eBay hands back a FLAT LIST. One call names every Item Specific for a category, each with a
// name, a required flag and a list of legal values, and that list is the whole truth.
//
// Amazon hands back a JSON SCHEMA. Every product belongs to a PRODUCT TYPE, and the product type
// carries a full JSON Schema document — nested objects, enums, string lengths, conditional
// requirements expressed as anyOf branches, and every scalar wrapped in an array-of-objects
// envelope carrying a marketplace and a language. A listing that does not validate against it is
// rejected, and Amazon does not tell you which part failed until after you have submitted it.
//
// So these shapes carry two things CategoryAspect does not: a TYPE (Amazon rejects the string
// "12" where it wants an integer, which eBay does not care about), and a distinction between
// required and CONDITIONALLY required — an attribute Amazon needs only when another one is absent.
// Reporting the second as plain "required" would tell a seller to fill in a product identifier
// they are exempt from; reporting it as optional would let them submit a listing that fails.

// ── A product type ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One product type as Amazon's <c>searchDefinitionsProductTypes</c> names it.
/// </summary>
/// <remarks>
/// <paramref name="Name"/> is the identifier every later call uses — <c>BLUETOOTH_SPEAKER</c>,
/// SNAKE_CASE, never localised. <paramref name="DisplayName"/> is the human label and is the one
/// that changes with the locale; Amazon has been known to omit it entirely, which is why nothing
/// here keys off it.
/// </remarks>
public sealed record AmazonProductType(string Name, string DisplayName, IReadOnlyList<string> MarketplaceIds)
{
    /// <summary>The label to show a person: Amazon's own, or the identifier made readable.</summary>
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Humanize(Name) : DisplayName;

    /// <summary><c>BLUETOOTH_SPEAKER</c> → <c>Bluetooth Speaker</c>.</summary>
    public static string Humanize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var words = name.Split(['_', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length == 1 ? w.ToUpperInvariant() : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant());
        return string.Join(' ', words);
    }
}

/// <summary>Which product type was chosen for a seller's words, and how sure that is.</summary>
/// <param name="Score">0 to 1. 1 is an exact match on the whole phrase.</param>
/// <param name="Why">In plain words, for the UI to show beside the choice.</param>
public sealed record AmazonProductTypeChoice(AmazonProductType ProductType, double Score, string Confidence, string Why);

/// <summary>The result of searching Amazon's product types for a seller's words.</summary>
public sealed class AmazonProductTypeSearchResult
{
    public string Query { get; set; } = "";

    /// <summary>ok | not_configured | no_match | ambiguous | error — see <see cref="AmazonDefinitionStatus"/>.</summary>
    public string Status { get; set; } = AmazonDefinitionStatus.Ok;
    public string Message { get; set; } = "";

    /// <summary>Every product type Amazon offered, in the order Amazon returned them.</summary>
    public List<AmazonProductType> Candidates { get; set; } = [];

    /// <summary>The one this app would use, or null when it refuses to pick between them.</summary>
    public AmazonProductTypeChoice? Chosen { get; set; }
}

// ── One attribute out of the schema ───────────────────────────────────────────────────────────

/// <summary>
/// One attribute of a product type, flattened out of Amazon's JSON Schema into something a form
/// can be built from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <see cref="Type"/> and <see cref="RawType"/> are both here.</b> Almost every attribute in
/// an Amazon product type schema is literally declared as an <c>array</c> of <c>object</c>, because
/// the same attribute can be given a different value per marketplace and per language:
/// </para>
/// <code>
/// "item_name": { "type": "array", "items": { "type": "object",
///     "properties": { "value": { "type": "string", "maxLength": 200 },
///                     "marketplace_id": {...}, "language_tag": {...} } } }
/// </code>
/// <para>
/// Reporting that as "array" is true and useless — what the seller fills in is a string of at most
/// 200 characters. So <see cref="Type"/> is the type of the value a person actually types, with the
/// envelope unwrapped, and <see cref="RawType"/> keeps what the schema literally said for anything
/// that has to build a payload Amazon will accept.
/// </para>
/// </remarks>
public sealed class AmazonAttribute
{
    /// <summary>The schema property name — <c>item_name</c>. This is what a submission is keyed by.</summary>
    public string Name { get; set; } = "";

    /// <summary>Amazon's own label — "Item Name". Falls back to the humanised property name.</summary>
    public string Title { get; set; } = "";

    /// <summary>Amazon's guidance for the attribute. Often the only place a rule is written down.</summary>
    public string Description { get; set; } = "";

    /// <summary>In the schema's root <c>required</c> list: Amazon rejects the listing without it.</summary>
    public bool Required { get; set; }

    /// <summary>
    /// Required only by one branch of a root-level <c>anyOf</c>/<c>oneOf</c> — needed unless a
    /// sibling attribute is supplied instead. See <see cref="RequirementNote"/>.
    /// </summary>
    public bool ConditionallyRequired { get; set; }

    /// <summary>Which alternative satisfies the requirement instead, when it is conditional.</summary>
    public string RequirementNote { get; set; } = "";

    /// <summary>The type of the value a seller supplies, envelope unwrapped: string, integer, number, boolean, object.</summary>
    public string Type { get; set; } = "";

    /// <summary>What the schema literally declares at the top level — nearly always <c>array</c>.</summary>
    public string RawType { get; set; } = "";

    /// <summary>Amazon published a closed list of values; anything else is rejected. Mirrors SELECTION_ONLY.</summary>
    public bool SelectionOnly { get; set; }

    /// <summary>More than one value may be supplied (bullet points, keywords).</summary>
    public bool MultiSelect { get; set; }

    /// <summary>Longest string Amazon accepts, or 0 when it did not say.</summary>
    public int MaxLength { get; set; }

    /// <summary>False when Amazon will not accept a change to this after the listing exists.</summary>
    public bool Editable { get; set; } = true;

    /// <summary>Amazon suggests keeping this out of a seller-facing form. Kept, not dropped — see the parser.</summary>
    public bool Hidden { get; set; }

    /// <summary>The property group Amazon files it under — "Product Identity", "Offer".</summary>
    public string Group { get; set; } = "";

    /// <summary>Amazon's accepted values, when it published a closed list.</summary>
    public List<string> Values { get; set; } = [];

    /// <summary>Display labels for <see cref="Values"/>, in the same order, when Amazon supplied them.</summary>
    public List<string> ValueLabels { get; set; } = [];

    /// <summary>Amazon's own examples. Worth carrying: they are the clearest statement of the format.</summary>
    public List<string> Examples { get; set; } = [];

    /// <summary>
    /// The sub-fields, when this attribute is a genuine composite rather than a single value —
    /// a price is an amount and a currency, a dimension is a number and a unit. Their own
    /// <see cref="Required"/> flags come from the nested schema, not the root one.
    /// </summary>
    public List<AmazonAttribute> Children { get; set; } = [];

    /// <summary>True when this attribute must be supplied one way or another.</summary>
    public bool IsRequiredSomehow => Required || ConditionallyRequired;

    /// <summary>The type as a person reads it: "string (max 200)", "one of 4 values", "object".</summary>
    public string TypeDescription
    {
        get
        {
            // "any of" rather than "one of" when more than one may be given. The difference is
            // between a dropdown and a set of checkboxes, and Amazon really does have both — the
            // dangerous-goods regulations are a select-all-that-apply, the condition is not.
            if (SelectionOnly && Values.Count > 0)
                return $"{Type} — {(MultiSelect ? "any" : "one")} of {Values.Count} " +
                       $"value{(Values.Count == 1 ? "" : "s")}";

            var text = string.IsNullOrEmpty(Type) ? "unspecified" : Type;
            if (MaxLength > 0) text += $" (max {MaxLength})";
            if (MultiSelect) text += ", repeatable";
            return text;
        }
    }
}

// ── A product type definition, fetched and parsed ─────────────────────────────────────────────

/// <summary>
/// Everything one product type requires, as this app understands it.
/// </summary>
/// <remarks>
/// <see cref="Attributes"/> is ordered required-first, then conditionally required, then the rest —
/// because that is the order the work has to be done in, and a form that lists 180 attributes
/// alphabetically buries the nine that decide whether the listing is accepted.
/// </remarks>
public sealed class AmazonProductTypeDefinition
{
    public string ProductType { get; set; } = "";
    public string DisplayName { get; set; } = "";

    /// <summary>Amazon's opaque schema version. The cache key that matters — see AmazonSchemaCache.</summary>
    public string Version { get; set; } = "";

    public string Locale { get; set; } = "";
    public string MarketplaceId { get; set; } = "";

    /// <summary>LISTING | LISTING_PRODUCT_ONLY | LISTING_OFFER_ONLY — which half of a listing this covers.</summary>
    public string Requirements { get; set; } = "";

    /// <summary>ENFORCED | NOT_ENFORCED. Amazon validates against the schema only when ENFORCED.</summary>
    public string RequirementsEnforced { get; set; } = "";

    /// <summary>Amazon's checksum of the schema document, used to know a cached copy is still the same one.</summary>
    public string SchemaChecksum { get; set; } = "";

    /// <summary>
    /// Where the schema document itself is. A short-lived pre-signed URL, NOT an SP-API path —
    /// see AmazonProductTypeService for why it must be fetched without the access token.
    /// </summary>
    public string SchemaUrl { get; set; } = "";

    public List<AmazonAttribute> Attributes { get; set; } = [];

    /// <summary>ok | not_configured | error | stale — see <see cref="AmazonDefinitionStatus"/>.</summary>
    public string Status { get; set; } = AmazonDefinitionStatus.Ok;
    public string Message { get; set; } = "";

    /// <summary>True when the schema came from disk rather than from Amazon on this call.</summary>
    public bool FromCache { get; set; }

    public IEnumerable<AmazonAttribute> RequiredAttributes =>
        Attributes.Where(a => a.Required);

    public IEnumerable<AmazonAttribute> ConditionallyRequiredAttributes =>
        Attributes.Where(a => a.ConditionallyRequired && !a.Required);

    public IEnumerable<AmazonAttribute> OptionalAttributes =>
        Attributes.Where(a => !a.IsRequiredSomehow);
}

/// <summary>
/// What happened when the app went looking. Mirrors <c>CategoryAspectsResult.Status</c>, and for the
/// same reason: "Amazon requires nothing" and "we could not ask Amazon" must never look alike.
/// </summary>
public static class AmazonDefinitionStatus
{
    public const string Ok            = "ok";
    /// <summary>This deployment cannot call Amazon at all — a credential is missing, not a lookup failure.</summary>
    public const string NotConfigured = "not_configured";
    /// <summary>Amazon answered, and offered nothing for these words.</summary>
    public const string NoMatch       = "no_match";
    /// <summary>Amazon offered several and none is clearly the one. The app refuses rather than guesses.</summary>
    public const string Ambiguous     = "ambiguous";
    /// <summary>Amazon refused or could not be reached.</summary>
    public const string Error         = "error";
    /// <summary>Served from the on-disk cache because Amazon could not be reached just now.</summary>
    public const string Stale         = "stale";
}
