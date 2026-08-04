using System.Globalization;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Fills the boxes on a listing that the app already knows the answer to.
/// </summary>
/// <remarks>
/// <para>
/// The readiness check could say a field was empty and nothing more. "No brand", "No package
/// weight", "No item location ZIP" — each one a sentence, a scroll, and a keystroke, on a screen
/// the seller is trying to get through a hundred times. For most of them the answer was already
/// in the room: the brand and the part number are in the title the app just wrote, the ZIP is in
/// Settings, and what a packed laptop weighs is what <see cref="PackageEstimator"/> exists to say.
/// </para>
/// <para>
/// Nothing here writes anything. It produces offers — value, source and confidence — that the
/// screen renders next to the finding they resolve. Two rules keep that honest, and both are
/// pinned by tests:
/// </para>
/// <list type="bullet">
///   <item>A field the seller filled in is never suggested over. Every rule below starts by
///   checking the field is empty, so "apply everything" can never overwrite a fact.</item>
///   <item>An inferred value is marked <see cref="FieldSuggestion.IsEstimate"/> and carries the
///   estimator's own basis. An estimated weight prices a shipping label, and a seller who is
///   never told which numbers were guessed learns to distrust all of them.</item>
/// </list>
/// <para>
/// Pure and static: no eBay call, no database, no clock. The identity, the package estimate and
/// the saved ZIP are all passed in already resolved.
/// </para>
/// </remarks>
public static class ListingAutofill
{
    public const string High   = "high";
    public const string Medium = "medium";
    public const string Low    = "low";

    /// <summary>Confidences the one-click fill is allowed to apply without being looked at.</summary>
    public static bool IsBulkFillable(FieldSuggestion s) =>
        s.Confidence is High or Medium;

    /// <summary>
    /// Everything the app can offer to fill on this draft, in the order the form asks for it.
    /// </summary>
    /// <param name="identity">Parsed from the seller's own title — null when it couldn't be read.</param>
    /// <param name="package">The estimator's answer for this item; null skips the shipping fields.</param>
    /// <param name="defaultPostalCode">The seller's saved location, blank when they have none.</param>
    /// <param name="category">Where the seller has filed items like this before; null when their
    /// history and eBay both had nothing to say.</param>
    public static List<FieldSuggestion> Suggest(
        ListingData listing,
        ProductIdentity? identity = null,
        PackageSpec? package = null,
        string defaultPostalCode = "",
        CategoryMatch? category = null)
    {
        var outp = new List<FieldSuggestion>();
        if (listing is null) return outp;

        // ── Category ──────────────────────────────────────────────────────────────────────────
        // First in the list because it is first on the form, and because it is the only empty
        // field that stops the rest of the check happening at all: eBay defines required Item
        // Specifics per category, so until this is answered the app cannot say what else is
        // missing. Filling it turns one blocker into a list the seller can finish.
        //
        // Two boxes, together. The visible one is a search field the seller types into and the
        // hidden one carries the ID that publishes; writing the name without the ID leaves a
        // category that looks chosen and publishes as nothing.
        if (string.IsNullOrWhiteSpace(listing.CategoryId) &&
            category is not null &&
            !string.IsNullOrWhiteSpace(category.CategoryId))
        {
            var display = string.IsNullOrWhiteSpace(category.CategoryName)
                ? "Category " + category.CategoryId
                : category.CategoryName;
            outp.Add(new FieldSuggestion
            {
                Field = "category", Label = "Category", Display = display,
                Source = category.Source,
                Confidence = category.Confidence is High or Medium ? category.Confidence : Medium,
                // The seller's own past choice is a reading. eBay's guess about a title is not —
                // it is marked, because a category decides which searches the listing appears in.
                IsEstimate = category.TimesUsed == 0,
                FieldId = "nl-category",
                Set =
                {
                    ["nl-category"]    = display,
                    ["nl-category-id"] = category.CategoryId,
                },
            });
        }

        // ── Brand ─────────────────────────────────────────────────────────────────────────────
        // Read out of the title, not inferred: the extractor only returns a brand it recognises
        // by name, so this is the seller's own word moved into the field eBay filters on.
        var brand = Clean(identity?.Brand);
        if (string.IsNullOrWhiteSpace(listing.Brand) && brand.Length > 0)
            outp.Add(new FieldSuggestion
            {
                Field = "brand", Label = "Brand", Display = brand,
                Source = "read out of your title", Confidence = High,
                FieldId = "nl-brand", Set = { ["nl-brand"] = brand },
            });

        // ── Part number ───────────────────────────────────────────────────────────────────────
        // Only offered when the listing has no identifier at all. A listing with a UPC does not
        // need this, and an MPN suggested next to a filled UPC reads as a second thing to do.
        var part = Clean(identity?.PartNumber);
        var hasIdentifier = !string.IsNullOrWhiteSpace(listing.Upc) ||
                            !string.IsNullOrWhiteSpace(listing.Ean) ||
                            !string.IsNullOrWhiteSpace(listing.Isbn) ||
                            !string.IsNullOrWhiteSpace(listing.Mpn);
        if (!hasIdentifier && part.Length > 0)
            outp.Add(new FieldSuggestion
            {
                Field = "mpn", Label = "MPN (part number)", Display = part,
                Source = "the part number in your title", Confidence = High,
                FieldId = "nl-mpn", Set = { ["nl-mpn"] = part },
            });

        // ── Item location ─────────────────────────────────────────────────────────────────────
        // Not a guess at all — it is the ZIP already saved in Settings, which a draft loaded from
        // a recovered autosave or an AI rewrite arrives without.
        var zip = Clean(defaultPostalCode);
        if (string.IsNullOrWhiteSpace(listing.ItemLocationPostalCode) && zip.Length > 0)
            outp.Add(new FieldSuggestion
            {
                Field = "postalCode", Label = "Item location ZIP", Display = zip,
                Source = "your saved default location", Confidence = High,
                FieldId = "nl-location-zip", Set = { ["nl-location-zip"] = zip },
            });

        if (package is null) return outp;

        // A package the estimator had to fall back on is a guess about an item it did not
        // recognise. It is still worth offering — a blank weight quotes nothing at all — but it
        // is offered on its own, never swept in by "fill everything".
        var packageConfidence = package.Source == "fallback" ? Low : Medium;

        // ── Package weight ────────────────────────────────────────────────────────────────────
        if (listing.WeightLbs <= 0 && listing.WeightOz <= 0 && package.WeightOz > 0)
        {
            var (lbs, oz) = SplitWeight(package.WeightOz);
            outp.Add(new FieldSuggestion
            {
                Field = "weight", Label = "Package weight",
                Display = $"{lbs} lb {oz} oz",
                Source = package.Basis, Confidence = packageConfidence, IsEstimate = true,
                FieldId = "nl-weight-lbs",
                Set =
                {
                    ["nl-weight-lbs"] = lbs.ToString(CultureInfo.InvariantCulture),
                    ["nl-weight-oz"]  = oz.ToString(CultureInfo.InvariantCulture),
                },
            });
        }

        // ── Package size ──────────────────────────────────────────────────────────────────────
        // All three or none: two sides of a box quote nothing, and a half-filled size reads as a
        // measurement somebody took.
        var hasDims = listing.PackageLengthIn > 0 && listing.PackageWidthIn > 0 && listing.PackageHeightIn > 0;
        if (!hasDims && package.LengthIn > 0 && package.WidthIn > 0 && package.HeightIn > 0)
            outp.Add(new FieldSuggestion
            {
                Field = "dimensions", Label = "Package size",
                Display = $"{Num(package.LengthIn)} × {Num(package.WidthIn)} × {Num(package.HeightIn)} in",
                Source = package.Basis, Confidence = packageConfidence, IsEstimate = true,
                FieldId = "nl-length",
                Set =
                {
                    ["nl-length"] = Num(package.LengthIn),
                    ["nl-width"]  = Num(package.WidthIn),
                    ["nl-height"] = Num(package.HeightIn),
                },
            });

        // ── Package type ──────────────────────────────────────────────────────────────────────
        // The one field here that has a value already — every new listing opens on whatever the
        // seller set as their default, and a 44 lb miner inherits "Thick Envelope" in silence.
        // Only ever suggested upward: the seller may know something packs flatter than the
        // estimate expects, but nobody benefits from a box quoted as smaller than it is.
        var suggestedType = SmallestFitting(package);
        var current = string.IsNullOrWhiteSpace(listing.PackageType) ? "PACKAGE_THICK_ENVELOPE" : listing.PackageType;
        if (package.Source != "fallback" && Rank(suggestedType) > Rank(current))
            outp.Add(new FieldSuggestion
            {
                Field = "packageType", Label = "Package type", Display = TypeLabel(suggestedType),
                Source = $"a {Num(package.LengthIn)} × {Num(package.WidthIn)} × {Num(package.HeightIn)} in box at "
                       + $"{SplitWeight(package.WeightOz).Lbs} lb doesn't ship as {TypeLabel(current)}",
                Confidence = Medium, IsEstimate = true,
                FieldId = "nl-package-type", Set = { ["nl-package-type"] = suggestedType },
            });

        return outp;
    }

    /// <summary>Ounces split into the pounds-and-ounces pair the form carries.</summary>
    public static (int Lbs, int Oz) SplitWeight(decimal totalOz)
    {
        var whole = (int)Math.Round(totalOz, MidpointRounding.AwayFromZero);
        if (whole < 0) whole = 0;
        return (whole / 16, whole % 16);
    }

    // ── eBay's package classes, smallest first ────────────────────────────────────────────────
    //
    // The limits are the thresholds at which a class stops being able to hold the box, not eBay's
    // rate card: this only decides which of the six words on the dropdown describes the parcel.
    // Anything past the last one is a very large package by definition.
    private static readonly (string Type, decimal MaxLongestIn, decimal MaxWeightOz)[] Classes =
    [
        ("LETTER",                      11.5m,    3.5m),
        ("LARGE_ENVELOPE_OR_FLAT_PACK", 15m,     16m),
        ("PACKAGE_THICK_ENVELOPE",      22m,    256m),   // 16 lb
        ("MAILING_BOX",                 30m,    640m),   // 40 lb
        ("BULKY_GOODS",                 46m,   1600m),   // 100 lb
        ("VERY_LARGE_PACKAGE",  decimal.MaxValue, decimal.MaxValue),
    ];

    /// <summary>The smallest of eBay's package classes the estimated box actually fits in.</summary>
    public static string SmallestFitting(PackageSpec spec)
    {
        var longest = Math.Max(spec.LengthIn, Math.Max(spec.WidthIn, spec.HeightIn));
        foreach (var (type, maxLongest, maxWeight) in Classes)
            if (longest <= maxLongest && spec.WeightOz <= maxWeight)
                return type;
        return "VERY_LARGE_PACKAGE";
    }

    /// <summary>Position in the list above; an unknown value sorts first so it is never "bigger".</summary>
    public static int Rank(string? packageType)
    {
        for (var i = 0; i < Classes.Length; i++)
            if (string.Equals(Classes[i].Type, packageType, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    // Matches the wording on the dropdown in index.html — a suggestion that names the option
    // differently from the option sends the seller looking for something that isn't there.
    private static string TypeLabel(string type) => type switch
    {
        "LETTER"                      => "Letter",
        "LARGE_ENVELOPE_OR_FLAT_PACK" => "Large Envelope / Flat Pack",
        "PACKAGE_THICK_ENVELOPE"      => "Package / Thick Envelope",
        "MAILING_BOX"                 => "Mailing Box",
        "BULKY_GOODS"                 => "Bulky Goods",
        "VERY_LARGE_PACKAGE"          => "Very Large Package",
        _                             => type,
    };

    private static string Clean(string? value) => (value ?? "").Trim();

    // Whole inches where the estimate is whole, one decimal where it isn't — "0.2" is a real
    // thickness for a graded card and rounding it to zero makes the box impossible.
    private static string Num(decimal value)
    {
        var rounded = Math.Round(value, 1);
        return rounded == Math.Truncate(rounded)
            ? ((int)rounded).ToString(CultureInfo.InvariantCulture)
            : rounded.ToString("0.#", CultureInfo.InvariantCulture);
    }
}
