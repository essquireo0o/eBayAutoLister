using System.Globalization;
using System.Text.Json.Nodes;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

// ── Reading an eBay draft onto an Amazon product type ─────────────────────────────────────────
//
// The AI already did the hard part. It looked at the photos, read the page, and produced a
// ListingData: a title, a brand, a part number, a price, a condition, a box, and a bag of Item
// Specifics. That work is marketplace-agnostic — the item does not change shape because it is being
// sold somewhere else. What changes is the FORM it has to be written on.
//
// eBay's form is flat. "Hashrate" is a name and "4.8 TH/s" is a string, and eBay takes it.
//
// Amazon's form is a schema. Every field is an array of objects; some are objects inside those;
// several are closed lists where "China" is not a legal value but "CN" is; and one of them is a
// choice — a product identifier OR an ASIN, never neither. So this file is a JOIN, not a
// conversion: for each attribute Amazon asks for, which thing the AI already extracted answers it.
//
// Three rules, and the third is the one that matters:
//
//   1. THE SCHEMA DECIDES THE SHAPE. Nothing here hard-codes what an attribute looks like. The
//      envelope, the selectors, the closed lists, the child fields and the length limits all come
//      from the parsed schema, so a product type this app has never seen still comes out right.
//
//   2. THE VALUE IS ALWAYS SOMETHING THE SELLER ALREADY SAID. Every filled attribute names where it
//      came from, in the seller's vocabulary, because they are the one answerable for the listing.
//
//   3. NOTHING IS INVENTED. Not a GTIN, not a brand, not a battery declaration, not a country of
//      origin. An unfillable required attribute comes back unfilled with a sentence saying why, and
//      the listing is reported as unable to go. That is the correct output, not a failure of one —
//      Amazon suspends accounts for fabricated product identifiers, and a blank field is visibly
//      blank in a way a plausible wrong one is not. NeverInvent below is the list, with reasons.
//
// This is the exact ethic AspectMatcher applies on the eBay side, and where the question is the
// same ("does this seller-typed key mean that marketplace-defined field?") this calls AspectMatcher
// rather than re-deciding it, so the two marketplaces cannot drift apart on what "Model" means.

/// <summary>
/// Fills one Amazon product type's attributes from an eBay draft the AI already produced.
/// </summary>
/// <remarks>
/// Pure and offline: a draft and a parsed product type in, a verdict and a payload out. No token, no
/// network, no seller account — which is what makes this phase provable at all, given that this
/// deployment cannot obtain an Amazon access token and the sandbox answers every product type query
/// with luggage.
/// </remarks>
public static class AmazonListingMapper
{
    /// <summary>The language every value is tagged with. This app is single-locale.</summary>
    public const string LanguageTag = "en_US";

    /// <summary>Amazon US. Used only when the caller could not supply the real one.</summary>
    public const string FallbackMarketplaceId = "ATVPDKIKX0DER";

    /// <summary>
    /// The selectors this app is able to answer.
    /// </summary>
    /// <remarks>
    /// Anything else Amazon lists as a selector is left off the payload rather than filled with a
    /// plausible default. <c>audience</c> is the live example: it chooses between an all-buyers
    /// price and a B2B one, omitting it means all buyers, and guessing "ALL" onto a seller who runs
    /// business pricing would publish the wrong price to the wrong buyers.
    /// </remarks>
    public static readonly string[] AnswerableSelectors = ["marketplace_id", "language_tag"];

    /// <summary>
    /// Attributes this app will never fill from an eBay draft, and the reason for each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counterpart of <see cref="AspectMatcher.NeverInfer"/>, and a harder rule: that one
    /// declines to GUESS a value from prose, this one declines even where a value is sitting right
    /// there looking like an answer. Each entry is a place where the obvious mapping is wrong:
    /// </para>
    /// <list type="bullet">
    /// <item><c>number_of_items</c> is how many units are in the box. The draft's Quantity is how
    /// many boxes are in stock. Twenty Antminers in a warehouse would list as a twenty-pack.</item>
    /// <item><c>item_dimensions</c> is the product's own size. The draft carries the SHIPPING BOX,
    /// which is bigger by whatever padding the seller used.</item>
    /// <item><c>batteries_required</c> and the dangerous-goods declaration are regulatory statements
    /// about lithium cells. A default is a false declaration to a carrier.</item>
    /// <item><c>merchant_suggested_asin</c> names an existing Amazon catalogue entry. A guessed one
    /// attaches this seller's offer to someone else's product.</item>
    /// </list>
    /// <para>
    /// These come back as unfilled required attributes with the reason attached. That is the phase
    /// working, not the phase failing.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> NeverInvent =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["number_of_items"] =
                "How many units are in one package. The draft's quantity is how many are in stock, " +
                "which is a different number — 20 miners on a shelf are not a 20-pack. Say how many " +
                "the buyer receives.",

            ["item_dimensions"] =
                "The product's own measurements. The draft has the shipping box, which is larger by " +
                "however much padding went round it, so it cannot stand in for this.",

            ["item_weight"] =
                "The product's own weight. The draft has the packed weight, which includes the box " +
                "and the padding.",

            ["batteries_required"] =
                "A regulatory declaration about batteries, not a description. Nothing in the draft " +
                "states it, and a default here is a false declaration to Amazon and to the carrier.",

            ["batteries_included"] =
                "A regulatory declaration about batteries, not a description. Nothing in the draft " +
                "states it.",

            ["supplier_declared_dg_hz_regulation"] =
                "Amazon's dangerous-goods declaration. It governs how the item may be shipped and " +
                "stored, and it is the seller's legal statement — this app has no basis to make it.",

            ["merchant_suggested_asin"] =
                "The ASIN of an existing Amazon catalogue entry. Nothing in an eBay draft is one, and " +
                "a guessed ASIN attaches this offer to somebody else's product.",

            ["fulfillment_availability"] =
                "Says whether Amazon or the seller ships this, and Amazon's own stock level is not " +
                "something an eBay draft knows. Choose the channel, then the quantity follows.",
        };

    // ── The join ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads <paramref name="listing"/> onto <paramref name="definition"/>'s attributes.
    /// </summary>
    /// <param name="marketplaceId">Stamped into every value's envelope. Falls back to the definition's.</param>
    /// <param name="sandboxNotice">Carried through untouched — see <see cref="AmazonSandboxNotice"/>.</param>
    public static AmazonListingFill Map(
        ListingData listing,
        AmazonProductTypeDefinition definition,
        string marketplaceId = "",
        string sandboxNotice = "")
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(definition);

        var fill = new AmazonListingFill
        {
            Status        = definition.Status,
            Message       = definition.Message,
            ProductType   = definition.ProductType,
            DisplayName   = definition.DisplayName,
            Locale        = string.IsNullOrWhiteSpace(definition.Locale) ? LanguageTag : definition.Locale,
            Version       = definition.Version,
            SourceTitle   = (listing.Title ?? "").Trim(),
            SandboxNotice = sandboxNotice ?? "",
            MarketplaceId = Pick(marketplaceId, definition.MarketplaceId, FallbackMarketplaceId),
        };

        // A definition that could not be read has no attributes to fill, and inventing an empty
        // one would report a listing as blocked on nothing rather than blocked on Amazon.
        if (definition.Status is not (AmazonDefinitionStatus.Ok or AmazonDefinitionStatus.Stale))
        {
            fill.Headline = string.IsNullOrWhiteSpace(definition.Message)
                ? "Amazon's requirements for this product type could not be read, so nothing can be filled in yet."
                : definition.Message;
            return fill;
        }

        var context = new DraftFacts(listing);

        foreach (var attribute in definition.Attributes)
            fill.Attributes.Add(FillOne(attribute, context, fill.MarketplaceId));

        // The either/or requirements, resolved AFTER every attribute has been attempted — the
        // question is about the group, and it cannot be answered one member at a time.
        fill.Choices = ResolveChoices(definition, fill.Attributes);

        foreach (var attribute in fill.Attributes)
            if (attribute.Payload is { } payload)
                fill.Payload[attribute.Name] = payload;

        fill.Headline = Headline(fill);
        return fill;
    }

    // ── One attribute ─────────────────────────────────────────────────────────

    private static AmazonFilledAttribute FillOne(
        AmazonAttribute attribute, DraftFacts facts, string marketplaceId)
    {
        var filled = new AmazonFilledAttribute
        {
            Name                  = attribute.Name,
            Title                 = attribute.Title,
            Required              = attribute.Required,
            ConditionallyRequired = attribute.ConditionallyRequired,
            RequirementNote       = attribute.RequirementNote,
        };

        // Amazon sets these itself and rejects a seller who sets them. Not a gap.
        if (attribute.Hidden || !attribute.Editable)
        {
            filled.State = AmazonFillState.Empty;
            filled.Note  = "Amazon sets this itself; it is not a seller field.";
            return filled;
        }

        if (NeverInvent.TryGetValue(attribute.Name, out var refusal))
        {
            filled.State = Unfilled(attribute);
            filled.Note  = refusal;
            return filled;
        }

        var found = AmazonDraftReader.Read(attribute, facts);
        if (found.Count == 0)
        {
            filled.State = Unfilled(attribute);
            filled.Note  = AmazonDraftReader.WhyNothing(attribute, facts);
            return filled;
        }

        filled.Source = found[0].Source;

        // Amazon caps how many values it takes. A sixth bullet point is a rejection, not a bullet
        // point Amazon drops, so the extras are cut here and said out loud.
        var cap = attribute.MaxCount > 0 ? attribute.MaxCount : found.Count;
        var dropped = Math.Max(0, found.Count - cap);
        var kept = found.Take(cap).ToList();

        var entries = new JsonArray();
        var notes = new List<string>();

        foreach (var candidate in kept)
        {
            var accepted = Accept(attribute, candidate, notes);
            if (accepted.Node is null)
            {
                filled.State = accepted.State;
                filled.Note  = accepted.Note;
                filled.Values = [candidate.Display];
                return filled;
            }

            filled.Values.Add(accepted.Display);
            entries.Add(Envelope(attribute, accepted.Node, marketplaceId));

            if (!string.IsNullOrWhiteSpace(candidate.Caution) &&
                !notes.Contains(candidate.Caution, StringComparer.Ordinal))
                notes.Add(candidate.Caution);
        }

        if (dropped > 0)
            notes.Add($"Amazon takes {cap}; the remaining {dropped} " +
                      $"{(dropped == 1 ? "value was" : "values were")} left off.");

        filled.State   = AmazonFillState.Filled;
        filled.Note    = string.Join(" ", notes);
        filled.Payload = attribute.RawType == "array" ? entries : entries.FirstOrDefault()?.DeepClone();
        return filled;
    }

    /// <summary>The state an attribute lands in when nothing answered it.</summary>
    private static string Unfilled(AmazonAttribute attribute) =>
        attribute.Required ? AmazonFillState.MissingRequired
        : attribute.ConditionallyRequired ? AmazonFillState.MissingConditional
        : AmazonFillState.Empty;

    // ── Amazon's own rules about the value ────────────────────────────────────

    private sealed record Accepted(JsonNode? Node, string Display, string State, string Note);

    /// <summary>
    /// Puts one found value through the schema's rules: closed list, length, type.
    /// </summary>
    /// <remarks>
    /// Every rejection path here withholds the value from the payload rather than sending it and
    /// hoping. Amazon does not say which attribute failed until after submission, so a value known
    /// to be illegal is worth strictly less than no value: it costs the same rejection and hides
    /// which field caused it.
    /// </remarks>
    private static Accepted Accept(AmazonAttribute attribute, DraftValue candidate, List<string> notes)
    {
        // A composite the reader built against the schema's own children. Its shape came from the
        // schema, so there is nothing here to re-check.
        if (candidate.Composite is { } composite)
            return new Accepted(composite, candidate.Display, AmazonFillState.Filled, "");

        var text = candidate.Text.Trim();

        // A closed list. Amazon rejects anything outside it, so the value has to BE one of Amazon's,
        // matched on its label as well as its token — a schema publishes "CN" and shows "China", and
        // the seller wrote the one they can see.
        if (attribute.SelectionOnly && attribute.Values.Count > 0)
        {
            var matched = MatchValue(text, attribute);
            if (matched is null)
                return new Accepted(null, text, AmazonFillState.InvalidValue,
                    $"The draft says \"{text}\", which is not one of the {attribute.Values.Count} values " +
                    $"Amazon accepts here ({Sample(attribute)}). Pick Amazon's word for it.");

            if (!string.Equals(matched, text, StringComparison.Ordinal))
                notes.Add($"\"{text}\" matched Amazon's \"{matched}\".");

            text = matched;
        }
        else if (attribute.MaxLength > 0 && text.Length > attribute.MaxLength)
        {
            // Prose can be shortened without becoming untrue. A name, a brand or a barcode cannot —
            // half a UPC is not a shorter UPC, it is a wrong one.
            if (!candidate.Prose)
                return new Accepted(null, text, AmazonFillState.TooLong,
                    $"{text.Length} characters, and Amazon's limit is {attribute.MaxLength}. Shortening " +
                    $"this would change what it says, so it is left for you to cut.");

            var cut = Shorten(text, attribute.MaxLength);
            notes.Add($"Cut from {text.Length} characters to Amazon's limit of {attribute.MaxLength}.");
            text = cut;
        }

        var node = ToNode(text, attribute.Type);
        return node is null
            ? new Accepted(null, text, AmazonFillState.InvalidValue,
                $"Amazon wants {attribute.Type} here and the draft says \"{text}\", which is not one.")
            : new Accepted(node, text, AmazonFillState.Filled, "");
    }

    /// <summary>Amazon's own token for a value the seller wrote, or null when it has none.</summary>
    /// <remarks>
    /// Tried against the tokens themselves and against the display labels, because a schema
    /// publishes <c>["US","CN"]</c> and shows "United States"/"China" — a seller writes what they
    /// were shown. Matching is on <see cref="AspectMatcher.NormalizeName"/> so spacing, case and
    /// punctuation stop mattering, exactly as they do for an eBay SELECTION_ONLY aspect.
    /// </remarks>
    public static string? MatchValue(string? text, AmazonAttribute attribute)
    {
        var wanted = AspectMatcher.NormalizeName(text);
        if (wanted.Length == 0) return null;

        for (var i = 0; i < attribute.Values.Count; i++)
            if (AspectMatcher.NormalizeName(attribute.Values[i]) == wanted)
                return attribute.Values[i];

        for (var i = 0; i < attribute.ValueLabels.Count && i < attribute.Values.Count; i++)
            if (AspectMatcher.NormalizeName(attribute.ValueLabels[i]) == wanted)
                return attribute.Values[i];

        return null;
    }

    /// <summary>A string as the JSON type the schema declares, or null when it is not one.</summary>
    private static JsonNode? ToNode(string text, string type) => type switch
    {
        "integer" => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? JsonValue.Create(i) : null,
        "number" => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? JsonValue.Create(d) : null,
        "boolean" => bool.TryParse(text, out var b) ? JsonValue.Create(b) : null,
        _ => JsonValue.Create(text),
    };

    /// <summary>Text cut to a length at a word boundary, with an ellipsis where it was cut.</summary>
    private static string Shorten(string text, int limit)
    {
        if (limit <= 1) return text[..limit];

        var cut = text[..(limit - 1)];
        var space = cut.LastIndexOf(' ');

        // Only back up to a word boundary when one is reasonably near the end. A single 400-character
        // word should lose its tail, not be replaced by an ellipsis.
        if (space > limit / 2) cut = cut[..space];
        return cut.TrimEnd(' ', ',', ';', '-', '.') + "…";
    }

    // ── Amazon's envelope ─────────────────────────────────────────────────────

    /// <summary>
    /// One value in the array-of-objects wrapper Amazon requires, selectors stamped.
    /// </summary>
    /// <remarks>
    /// A scalar becomes <c>{ "value": … }</c>; a composite is already the object and only needs the
    /// selectors adding. Which selectors come from the attribute, not from a constant, so an
    /// attribute Amazon does not language-tag does not get a language tag.
    /// </remarks>
    private static JsonNode Envelope(AmazonAttribute attribute, JsonNode value, string marketplaceId)
    {
        var entry = value as JsonObject ?? new JsonObject { ["value"] = value };

        foreach (var selector in attribute.Selectors)
        {
            if (!AnswerableSelectors.Contains(selector, StringComparer.Ordinal)) continue;
            if (entry.ContainsKey(selector)) continue;

            entry[selector] = selector switch
            {
                "marketplace_id" => marketplaceId,
                "language_tag"   => LanguageTag,
                _                => null,
            };
        }

        return entry;
    }

    // ── Either/or requirements ────────────────────────────────────────────────

    /// <summary>
    /// Amazon's <c>anyOf</c> requirements as groups, and which member (if any) answered each.
    /// </summary>
    /// <remarks>
    /// Members are joined transitively: if the identifier lists the ASIN as its alternative and the
    /// ASIN lists the identifier as its own, they are one requirement with two doors, not two
    /// requirements. Attributes whose group is satisfied are re-stated as
    /// <see cref="AmazonFillState.SatisfiedByAlternative"/> so nothing reports as missing that is
    /// not actually needed.
    /// </remarks>
    private static List<AmazonRequirementChoice> ResolveChoices(
        AmazonProductTypeDefinition definition, List<AmazonFilledAttribute> filled)
    {
        var byName = filled.ToDictionary(a => a.Name, StringComparer.Ordinal);
        var choices = new List<AmazonRequirementChoice>();
        var placed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var attribute in definition.Attributes)
        {
            if (!attribute.ConditionallyRequired || attribute.Required) continue;
            if (!placed.Add(attribute.Name)) continue;

            var members = new List<string> { attribute.Name };
            foreach (var alternative in attribute.Alternatives)
                if (placed.Add(alternative)) members.Add(alternative);

            var satisfiedBy = members.FirstOrDefault(m =>
                byName.TryGetValue(m, out var f) && f.IsFilled) ?? "";

            var readable = members.Select(m =>
                byName.TryGetValue(m, out var f) && !string.IsNullOrWhiteSpace(f.Title) ? f.Title : m).ToList();

            choices.Add(new AmazonRequirementChoice
            {
                Options     = members,
                SatisfiedBy = satisfiedBy,
                Note = satisfiedBy.Length > 0
                    ? $"Satisfied by {satisfiedBy}. The other " +
                      $"{(members.Count == 2 ? "option is" : "options are")} not needed."
                    : $"Amazon needs one of these and the draft has none: {string.Join(", ", readable)}.",
            });

            // A door that is open makes the others unnecessary rather than missing.
            if (satisfiedBy.Length > 0)
                foreach (var member in members)
                    if (member != satisfiedBy && byName.TryGetValue(member, out var f) &&
                        f.State == AmazonFillState.MissingConditional)
                    {
                        f.State = AmazonFillState.SatisfiedByAlternative;
                        f.Note  = $"Not needed — {satisfiedBy} answers this requirement.";
                    }
        }

        return choices;
    }

    // ── Saying where it stands ────────────────────────────────────────────────

    private static string Headline(AmazonListingFill fill)
    {
        var required = fill.RequiredCount;
        var done = fill.RequiredFilledCount;
        var product = string.IsNullOrWhiteSpace(fill.DisplayName) ? fill.ProductType : fill.DisplayName;

        if (fill.CanSubmit)
            return $"Ready: all {required} of Amazon's required attributes for {product} are filled " +
                   $"from the draft.";

        var open = fill.Blocking.Count();
        var unmet = fill.Choices.Count(c => !c.Satisfied);

        var reasons = new List<string>();
        if (open > 0) reasons.Add($"{open} required attribute{(open == 1 ? "" : "s")} " +
                                  $"{(open == 1 ? "has" : "have")} no value");
        if (unmet > 0) reasons.Add($"{unmet} either/or requirement{(unmet == 1 ? " is" : "s are")} unmet");

        return $"Not ready: {done} of {required} required attributes for {product} filled from the draft — " +
               string.Join(", ", reasons) + ". Nothing was invented to close the gap.";
    }

    private static string Sample(AmazonAttribute attribute)
    {
        var shown = attribute.Values.Take(4).ToList();
        var text = string.Join(", ", shown);
        return attribute.Values.Count > shown.Count ? text + ", …" : text;
    }

    private static string Pick(params string[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim() ?? "";
}
