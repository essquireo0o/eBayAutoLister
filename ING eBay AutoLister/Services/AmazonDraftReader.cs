using System.Globalization;
using System.Text.Json.Nodes;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

// ── Which thing the AI extracted answers which Amazon attribute ───────────────────────────────
//
// AmazonListingMapper owns the rules Amazon imposes — the envelope, the closed lists, the lengths,
// the either/or requirements. This file owns the only other question: given an attribute called
// item_name, or purchasable_offer, or country_of_origin, WHAT ON THE DRAFT ANSWERS IT.
//
// It is separate because the two halves are wrong in different ways and are fixed by different
// people. Getting the envelope wrong breaks every listing identically and is found by the first
// submission. Getting the join wrong — mapping stock quantity onto number_of_items — produces a
// listing Amazon accepts and a buyer complains about, and is found by a refund. So the joins live
// somewhere they can be read as a list and argued with one line at a time.
//
// Two kinds of join live here, and the second is the one that scales:
//
//   NAMED. A table of Amazon attribute names against draft fields, for the attributes where the
//   answer is structural rather than lexical — purchasable_offer is a price wrapped in a schedule
//   wrapped in a currency, and no amount of name matching finds that.
//
//   BY NAME. Everything else goes through AspectMatcher, the same matcher eBay's Item Specifics use,
//   with the draft's own specifics standing in as the vocabulary. "Color" answers `color`, "Power
//   Consumption" answers `power_consumption`, and "Country of Manufacture" answers
//   `country_of_origin` through the alias group both marketplaces already share. Matching is
//   direction-agnostic and refuses ties, so an unrecognised attribute comes back empty rather than
//   wrong — which is the failure this whole phase is arranged to have.

/// <summary>One value found on a draft, before Amazon's rules have been applied to it.</summary>
public sealed class DraftValue
{
    /// <summary>The value, for an attribute that takes a single one.</summary>
    public string Text { get; init; } = "";

    /// <summary>The value, for an attribute the schema declares as a composite. Built to its children.</summary>
    public JsonObject? Composite { get; init; }

    /// <summary>How it reads to a person. Defaults to <see cref="Text"/>.</summary>
    public string Display { get; init; } = "";

    /// <summary>Where on the draft it came from, in the seller's vocabulary.</summary>
    public string Source { get; init; } = "";

    /// <summary>
    /// What the seller should check about this value, when the join is defensible but not obvious.
    /// </summary>
    /// <remarks>
    /// The middle ground between filling a field and refusing to. Some attributes have a source on
    /// the draft that is the right one nine times in ten and the wrong one the tenth — Amazon reads
    /// <c>list_price</c> as the manufacturer's struck-through price, and the draft has an asking
    /// price. Refusing would leave a required field blank over a caveat; filling it silently would
    /// publish an invented discount. So it is filled, and the caveat is attached to it.
    /// </remarks>
    public string Caution { get; init; } = "";

    /// <summary>
    /// True when shortening the value keeps it true — a description, a feature line.
    /// </summary>
    /// <remarks>
    /// False is the default and the safe one. A brand, a part number or a barcode cut to length is
    /// not a shorter version of itself, it is a different and wrong value.
    /// </remarks>
    public bool Prose { get; init; }

    public string Shown => string.IsNullOrEmpty(Display) ? Text : Display;
}

/// <summary>
/// The draft, pre-chewed into the forms the joins need.
/// </summary>
/// <remarks>
/// Computed once per fill rather than per attribute: a product type has on the order of 150
/// attributes, and stripping the same HTML description 150 times to answer three of them is work
/// nobody asked for.
/// </remarks>
public sealed class DraftFacts
{
    public DraftFacts(ListingData listing, IReadOnlyDictionary<string, string>? sellerAnswers = null)
    {
        ArgumentNullException.ThrowIfNull(listing);
        Listing = listing;

        // Ordinal, unlike Specifics above, and that difference is deliberate: an Item Specific is a
        // name a person typed and "power consumption" ought to find "Power Consumption", whereas a
        // key here is Amazon's own schema property name and there is exactly one spelling of it.
        SellerAnswers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in sellerAnswers ?? new Dictionary<string, string>())
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                SellerAnswers[key.Trim()] = value.Trim();

        PlainDescription = CrossListingExporter.HtmlToText(listing.Description);

        Specifics = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in listing.ItemSpecifics ?? [])
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                Specifics[key.Trim()] = value.Trim();

        // The specifics as a vocabulary AspectMatcher can be pointed at. Values are left empty
        // deliberately — this is a name lookup, and giving it a closed list would make it start
        // validating against the seller's own words.
        SpecificNames = [.. Specifics.Keys.Select(k => new CategoryAspect { Name = k })];

        Bullets = BuildBullets();
    }

    public ListingData Listing { get; }

    /// <summary>The description with eBay's HTML taken out. Amazon's description field is plain text.</summary>
    public string PlainDescription { get; }

    /// <summary>The draft's Item Specifics, trimmed, blanks dropped, matched case-insensitively.</summary>
    public Dictionary<string, string> Specifics { get; }

    /// <summary>
    /// What the seller answered themselves, keyed by Amazon's schema property name.
    /// </summary>
    /// <remarks>
    /// Not read by <see cref="AmazonDraftReader"/> — nothing in here came off the draft, so reading
    /// it as a draft fact would report a person's declaration as something the app found. It is
    /// consumed one level up, in <see cref="AmazonListingMapper"/>, where the source can be recorded
    /// as the human it is.
    /// </remarks>
    public Dictionary<string, string> SellerAnswers { get; }

    /// <summary>Feature lines for <c>bullet_point</c>, best first.</summary>
    public List<string> Bullets { get; }

    private List<CategoryAspect> SpecificNames { get; }

    /// <summary>
    /// The Item Specific that means <paramref name="amazonName"/>, or null.
    /// </summary>
    /// <remarks>
    /// Amazon's <c>power_consumption</c> and eBay's "Power Consumption" normalise to the same thing,
    /// and where they do not, the alias groups the eBay side already maintains carry the rest.
    /// Ambiguity returns null — <see cref="AspectMatcher.MatchAspectName"/> insists the answer is
    /// unique, which is what stops <c>compatible_brand</c> collecting the seller's "Brand".
    /// </remarks>
    public (string Key, string Value)? Specific(string amazonName)
    {
        var matched = AspectMatcher.MatchAspectName(amazonName, SpecificNames);
        if (matched is null) return null;
        return Specifics.TryGetValue(matched.Name, out var value) ? (matched.Name, value) : null;
    }

    /// <summary>Total packed weight in pounds, or 0.</summary>
    public decimal PackageWeightPounds => Listing.WeightLbs + Listing.WeightOz / 16m;

    /// <summary>
    /// Up to a dozen feature lines: the description's own bullets first, then the Item Specifics.
    /// </summary>
    /// <remarks>
    /// The description's own bullets come first because the AI wrote them for a buyer to read and
    /// the Item Specifics are a lookup table — both end up as "Label: value" often enough, but only
    /// one of them was composed. Within each group the draft's order is kept rather than reordered
    /// by some guess at importance, so what Amazon's cap admits is the top of the seller's own list
    /// and they can change it by moving a line.
    /// </remarks>
    private List<string> BuildBullets()
    {
        var bullets = new List<string>();

        foreach (var line in PlainDescription.Split('\n'))
        {
            var text = line.Trim();
            if (!text.StartsWith('•')) continue;

            text = text[1..].Trim();
            if (text.Length > 0 && !bullets.Contains(text, StringComparer.OrdinalIgnoreCase))
                bullets.Add(text);
        }

        foreach (var (key, value) in Specifics)
        {
            var text = $"{key}: {value}";
            if (!bullets.Contains(text, StringComparer.OrdinalIgnoreCase)) bullets.Add(text);
        }

        return bullets;
    }
}

/// <summary>Finds the value on a draft that answers one Amazon attribute.</summary>
public static class AmazonDraftReader
{
    /// <summary>
    /// eBay's condition grades against Amazon's <c>condition_type</c> tokens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The candidate is matched against the product type's own enum before it is used, so a category
    /// that publishes a different set simply fails to match and says so rather than sending a token
    /// Amazon rejects.
    /// </para>
    /// <para>
    /// <b>FOR_PARTS_OR_NOT_WORKING has no entry, deliberately.</b> Amazon has no for-parts grade, and
    /// the nearest one — Used, Acceptable — describes a working item with cosmetic damage. Grading a
    /// non-working item into it is a misdescription the buyer discovers on arrival. The flat-file
    /// exporter in CrossListingExporter does downgrade it, with a warning attached, because a
    /// spreadsheet a human reviews before uploading is a different artefact from a payload; this
    /// builds the payload, so it refuses.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> ConditionTokens =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NEW"]             = "new_new",
            ["LIKE_NEW"]        = "used_like_new",
            ["USED_EXCELLENT"]  = "used_like_new",
            ["USED_VERY_GOOD"]  = "used_very_good",
            ["USED_GOOD"]       = "used_good",
            ["USED_ACCEPTABLE"] = "used_acceptable",
        };

    /// <summary>The currency every price is stated in. This app sells on Amazon US.</summary>
    public const string Currency = "USD";

    /// <summary>The values that answer <paramref name="attribute"/>, or an empty list.</summary>
    public static List<DraftValue> Read(AmazonAttribute attribute, DraftFacts facts)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(facts);

        var listing = facts.Listing;

        return attribute.Name switch
        {
            "item_name" => One(listing.Title, "the draft title"),

            "brand" or "manufacturer" =>
                One(listing.Brand, "the draft's brand") is { Count: > 0 } fromField
                    ? fromField
                    : FromSpecific(attribute, facts),

            "model_name" =>
                FromSpecific(attribute, facts) is { Count: > 0 } fromSpecific
                    ? fromSpecific
                    : One(listing.Mpn, "the draft's MPN"),

            "model_number" or "part_number" =>
                One(listing.Mpn, "the draft's MPN") is { Count: > 0 } fromMpn
                    ? fromMpn
                    : FromSpecific(attribute, facts),

            "product_description" => One(facts.PlainDescription, "the draft description", prose: true),

            "bullet_point" => facts.Bullets
                .Select(b => new DraftValue { Text = b, Source = "the draft's features and specifics", Prose = true })
                .ToList(),

            "condition_type" => Condition(listing),

            "condition_note" => One(listing.ConditionDescription, "the draft's condition notes", prose: true),

            "externally_assigned_product_identifier" => ProductIdentifier(attribute, listing),

            "list_price" => Money(attribute, listing.Price, "the draft's asking price",
                "Amazon reads list_price as the manufacturer's list price — the struck-through one. " +
                "This is the draft's asking price, which is the same number only if the item is not " +
                "discounted. Where it has an RRP, put that here; the price you charge is in " +
                "purchasable_offer either way."),

            "purchasable_offer" => PurchasableOffer(attribute, listing),

            "item_package_weight" => Measurement(
                attribute, facts.PackageWeightPounds, "pounds", "the draft's package weight"),

            "item_package_dimensions" => PackageDimensions(attribute, listing),

            "main_product_image_locator" => Image(attribute, listing, 0),

            _ when attribute.Name.StartsWith("other_product_image_locator_", StringComparison.Ordinal) &&
                   int.TryParse(attribute.Name["other_product_image_locator_".Length..], out var nth) =>
                Image(attribute, listing, nth),

            // Everything else is a name-matching question, and the eBay side already answers it.
            _ => FromSpecific(attribute, facts),
        };
    }

    /// <summary>
    /// Why nothing answered <paramref name="attribute"/>, in a sentence a seller can act on.
    /// </summary>
    /// <remarks>
    /// The general answer is deliberately dull. The named ones exist where "missing" would send a
    /// seller in a wrong direction — most of all for the product identifier, where the fastest way
    /// to make the field go green is to type a plausible barcode, and doing so is how sellers lose
    /// accounts.
    /// </remarks>
    public static string WhyNothing(AmazonAttribute attribute, DraftFacts facts)
    {
        var condition = (facts.Listing.Condition ?? "").Trim().ToUpperInvariant();

        return attribute.Name switch
        {
            "externally_assigned_product_identifier" =>
                "The draft has no UPC, EAN or ISBN. Amazon will not create a new listing without a " +
                "product identifier unless your account holds a GTIN exemption for the brand. Find the " +
                "real barcode or apply for the exemption — a made-up one is an account suspension, not " +
                "a rejected listing.",

            "brand" or "manufacturer" =>
                "The draft has no brand. Amazon requires one and accepts \"Generic\" for genuinely " +
                "unbranded goods — but that is a claim about the product, so it has to be yours to make.",

            "condition_type" when condition == "FOR_PARTS_OR_NOT_WORKING" =>
                "The draft is graded for parts or not working, and Amazon has no such condition. Its " +
                "lowest grade, Used - Acceptable, means a working item with wear, so this cannot be " +
                "mapped without misdescribing it. Most categories forbid selling non-working items at " +
                "all — this one belongs on eBay.",

            "condition_type" =>
                "The draft has no condition set.",

            "item_name" =>
                "The draft has no title.",

            "product_description" =>
                "The draft has no description.",

            _ when attribute.SelectionOnly && attribute.Values.Count > 0 =>
                $"Nothing in the draft answers this. Amazon accepts {attribute.Values.Count} values " +
                $"here, so it has to be picked rather than typed.",

            _ =>
                "Nothing in the draft's fields or Item Specifics answers this.",
        };
    }

    // ── The structural joins ──────────────────────────────────────────────────

    private static List<DraftValue> Condition(ListingData listing)
    {
        var grade = (listing.Condition ?? "").Trim().ToUpperInvariant();
        return ConditionTokens.TryGetValue(grade, out var token)
            ? [new DraftValue { Text = token, Source = $"the draft's condition ({grade})" }]
            : [];
    }

    /// <summary>
    /// A UPC, EAN or ISBN as Amazon's identifier composite — the number and which kind it is.
    /// </summary>
    /// <remarks>
    /// The precedence matches <c>CrossListingExporter.GtinFor</c>, so a draft exported to a flat file
    /// and the same draft submitted through the API claim the same identifier. The <c>type</c> token
    /// is matched against the schema's own enum rather than assumed: Amazon spells it "upc" here and
    /// has spelt it otherwise elsewhere, and an unmatched spelling is caught as an invalid value
    /// instead of being submitted.
    /// </remarks>
    private static List<DraftValue> ProductIdentifier(AmazonAttribute attribute, ListingData listing)
    {
        var (number, kind, field) =
            !string.IsNullOrWhiteSpace(listing.Upc)  ? (listing.Upc.Trim(),  "upc",  "UPC") :
            !string.IsNullOrWhiteSpace(listing.Ean)  ? (listing.Ean.Trim(),  "ean",  "EAN") :
            !string.IsNullOrWhiteSpace(listing.Isbn) ? (listing.Isbn.Trim(), "isbn", "ISBN") :
            ("", "", "");

        if (number.Length == 0) return [];

        var typeChild = Child(attribute, "type");
        var token = typeChild is null ? kind : AmazonListingMapper.MatchValue(kind, typeChild);
        if (token is null) return [];

        return
        [
            new DraftValue
            {
                Composite = new JsonObject { ["value"] = number, ["type"] = token },
                Display   = $"{number} ({field})",
                Source    = $"the draft's {field}",
            },
        ];
    }

    /// <summary>An amount and its currency, against whatever the schema calls them.</summary>
    private static List<DraftValue> Money(
        AmazonAttribute attribute, decimal amount, string source, string caution = "")
    {
        if (amount <= 0) return [];

        var currencyChild = Child(attribute, "currency");
        var currency = currencyChild is null ? Currency : AmazonListingMapper.MatchValue(Currency, currencyChild);
        if (currency is null) return [];

        return
        [
            new DraftValue
            {
                Composite = new JsonObject { ["value"] = amount, ["currency"] = currency },
                Display   = $"{amount.ToString("0.00", CultureInfo.InvariantCulture)} {currency}",
                Source    = source,
                Caution   = caution,
            },
        ];
    }

    /// <summary>
    /// The selling price, in the shape Amazon actually charges from.
    /// </summary>
    /// <remarks>
    /// <c>purchasable_offer</c> is a currency wrapping a price list wrapping a schedule, and the
    /// nesting is Amazon's, not a generalisation of it — an open-ended schedule is one entry with a
    /// price and no start date, which is what an ordinary always-on offer is. <c>audience</c> is left
    /// off: omitted means all buyers, and filling it in would be this app choosing between consumer
    /// and business pricing on the seller's behalf.
    /// </remarks>
    private static List<DraftValue> PurchasableOffer(AmazonAttribute attribute, ListingData listing)
    {
        if (listing.Price <= 0) return [];

        var currencyChild = Child(attribute, "currency");
        var currency = currencyChild is null ? Currency : AmazonListingMapper.MatchValue(Currency, currencyChild);
        if (currency is null) return [];

        return
        [
            new DraftValue
            {
                Composite = new JsonObject
                {
                    ["currency"]  = currency,
                    ["our_price"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["schedule"] = new JsonArray
                            {
                                new JsonObject { ["value_with_tax"] = listing.Price },
                            },
                        },
                    },
                },
                Display = $"{listing.Price.ToString("0.00", CultureInfo.InvariantCulture)} {currency}",
                Source  = "the draft's price",
            },
        ];
    }

    /// <summary>A number and its unit, with the unit matched against the schema's own list.</summary>
    private static List<DraftValue> Measurement(
        AmazonAttribute attribute, decimal amount, string unit, string source)
    {
        if (amount <= 0) return [];

        var node = MeasurementObject(attribute, amount, unit);
        if (node is null) return [];

        return
        [
            new DraftValue
            {
                Composite = node,
                Display   = $"{Trim(amount)} {unit}",
                Source    = source,
            },
        ];
    }

    /// <summary>The shipping box, as three measurements against the schema's own child names.</summary>
    /// <remarks>
    /// The PACKAGE, never the item — <c>item_dimensions</c> is refused outright in
    /// <see cref="AmazonListingMapper.NeverInvent"/> for exactly this reason. A box is the product
    /// plus however much bubble wrap went round it, and the two are not the same measurement.
    /// </remarks>
    private static List<DraftValue> PackageDimensions(AmazonAttribute attribute, ListingData listing)
    {
        var sides = new (string Name, decimal Value)[]
        {
            ("length", listing.PackageLengthIn),
            ("width",  listing.PackageWidthIn),
            ("height", listing.PackageHeightIn),
        };

        // Amazon takes the box or none of it. Two sides out of three is not a smaller box.
        if (sides.Any(s => s.Value <= 0)) return [];

        var composite = new JsonObject();
        foreach (var (name, value) in sides)
        {
            var child = Child(attribute, name);
            if (child is null) return [];

            var node = MeasurementObject(child, value, "inches");
            if (node is null) return [];

            composite[name] = node;
        }

        return
        [
            new DraftValue
            {
                Composite = composite,
                Display   = $"{Trim(listing.PackageLengthIn)} × {Trim(listing.PackageWidthIn)} × " +
                            $"{Trim(listing.PackageHeightIn)} inches",
                Source    = "the draft's package dimensions",
            },
        ];
    }

    private static JsonObject? MeasurementObject(AmazonAttribute attribute, decimal amount, string unit)
    {
        var unitChild = Child(attribute, "unit");
        var matched = unitChild is null ? unit : AmazonListingMapper.MatchValue(unit, unitChild);

        // Amazon publishes the units it takes. If inches is not among them, converting silently
        // would be this app changing a measurement the seller entered.
        return matched is null ? null : new JsonObject { ["value"] = amount, ["unit"] = matched };
    }

    private static List<DraftValue> Image(AmazonAttribute attribute, ListingData listing, int index)
    {
        var urls = listing.ImageUrls ?? [];
        if (index >= urls.Count || string.IsNullOrWhiteSpace(urls[index])) return [];

        var child = Child(attribute, "media_location");
        if (child is null) return [];

        return
        [
            new DraftValue
            {
                Composite = new JsonObject { ["media_location"] = urls[index].Trim() },
                Display   = urls[index].Trim(),
                Source    = index == 0 ? "the draft's first photo" : $"the draft's photo {index + 1}",
            },
        ];
    }

    // ── The name-matching join ────────────────────────────────────────────────

    /// <summary>
    /// The Item Specific whose name means this attribute, when there is exactly one.
    /// </summary>
    /// <remarks>
    /// Only for attributes that take a plain value. A composite needs its children filled and a
    /// single string cannot do it, so those come back empty and are reported as unanswered rather
    /// than half-built.
    /// </remarks>
    private static List<DraftValue> FromSpecific(AmazonAttribute attribute, DraftFacts facts)
    {
        if (attribute.Children.Count > 0 || attribute.Type == "object") return [];

        var found = facts.Specific(attribute.Name);
        if (found is not { } hit) return [];

        return [new DraftValue { Text = hit.Value, Source = $"Item Specific \"{hit.Key}\"" }];
    }

    // ── Small things ──────────────────────────────────────────────────────────

    private static List<DraftValue> One(string? text, string source, bool prose = false) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : [new DraftValue { Text = text.Trim(), Source = source, Prose = prose }];

    private static AmazonAttribute? Child(AmazonAttribute attribute, string name) =>
        attribute.Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    private static string Trim(decimal value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);
}
