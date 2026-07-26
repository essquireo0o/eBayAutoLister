using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

// One question, answered before the seller presses Publish: "will this list, and will anyone
// find it?"
//
// The app's Publish button used to check two things — a title and a price above zero — and hand
// everything else to eBay, which answers minutes later with a raw API error. This turns that
// round trip into a checklist that exists *while the listing is being written*.
//
// Two kinds of finding, kept strictly apart because they cost different things:
//
//   BLOCKERS  — eBay rejects the publish, or the listing cannot go live. Nothing sells.
//   WARNINGS  — it lists fine and earns less: fewer photos, an unused half of the title, missing
//               specifics that buyers' left-hand filters run on. eBay's search surfaces listings
//               by Item Specifics, so a blank one isn't a cosmetic gap — it removes the listing
//               from the filtered searches most buyers actually use.
//
// Every finding carries the field it lives in (so the UI can jump to it) and one sentence of why
// it matters. A checklist without the "why" is nagging, and gets ignored, and then the listing
// goes out at 60% readiness anyway.
//
// Pure and static: no eBay call, no clock, no database. The aspects are passed in already fetched.
public static class ListingReadinessAnalyzer
{
    // eBay's hard title limit. Over it, the publish fails.
    public const int MaxTitleLength = 80;

    // Under this, the seller is throwing away keyword space they're allowed to use for free.
    // eBay matches buyer searches against title words; unused characters are unmatched searches.
    public const int ThinTitleLength = 45;

    // eBay gives 24 photo slots and charges nothing for them. Listings with several photos
    // convert better and get fewer "does it include…" questions, which is time as well as money.
    public const int GoodPhotoCount = 4;
    public const int IdealPhotoCount = 6;

    public const int ThinDescriptionLength = 200;

    // Required specifics dominate the score because they are the difference between a live
    // listing and an error message. The cap stops a category with 12 required aspects from
    // reporting a score of zero on a listing that is otherwise finished.
    private const int MissingRequiredCost = 12;
    private const int MissingRequiredCap  = 48;
    private const int InvalidValueCost    = 10;
    private const int InvalidValueCap     = 30;
    private const int MissingRecommendedCost = 3;
    private const int MissingRecommendedCap  = 12;

    public static ListingReadinessResult Analyze(
        ListingData listing,
        IReadOnlyList<AspectField> aspects,
        string aspectStatus = "ok",
        string aspectMessage = "",
        IReadOnlyList<string>? customAspectNames = null)
    {
        var fixes = new List<ReadinessFix>();
        var score = 100;

        void Add(string id, string severity, string label, string why, string fieldId, int cost,
                 bool autoFixable = false, string aspectName = "")
        {
            fixes.Add(new ReadinessFix
            {
                Id = id, Severity = severity, Label = label, Why = why,
                FieldId = fieldId, ScoreCost = cost, AutoFixable = autoFixable, AspectName = aspectName,
            });
            score -= cost;
        }

        var title = (listing.Title ?? "").Trim();
        var descriptionText = AspectMatcher.StripHtml(listing.Description);
        var photos = (listing.ImageUrls ?? []).Where(u => !string.IsNullOrWhiteSpace(u)).ToList();

        // ── Blockers ──────────────────────────────────────────────────────────────────────────

        if (title.Length == 0)
            Add("title-missing", FixSeverity.Blocker, "Add a title",
                "eBay will not accept a listing without one.", "nl-title", 25);
        else if (title.Length > MaxTitleLength)
            Add("title-too-long", FixSeverity.Blocker,
                $"Title is {title.Length} characters — eBay's limit is {MaxTitleLength}",
                "The publish is rejected outright above 80 characters.", "nl-title", 15);

        if (listing.Price <= 0)
            Add("price-missing", FixSeverity.Blocker, "Set a price",
                "A listing with no price cannot go live.", "nl-price", 25);

        if (string.IsNullOrWhiteSpace(listing.CategoryId))
            Add("category-missing", FixSeverity.Blocker, "Pick a category",
                "eBay needs one to publish — and the category decides which Item Specifics are required, so nothing below can be checked without it.",
                "nl-category", 25);

        if (photos.Count == 0)
            Add("photos-missing", FixSeverity.Blocker, "Add at least one photo",
                "eBay requires a photo, and buyers scroll straight past listings without one.",
                "nl-photo-grid", 20);

        // Required Item Specifics — the reason this whole feature exists.
        var missingRequired = aspects
            .Where(a => a.State == AspectState.MissingRequired)
            .ToList();

        var requiredSpent = 0;
        foreach (var a in missingRequired)
        {
            var cost = Math.Min(MissingRequiredCost, Math.Max(0, MissingRequiredCap - requiredSpent));
            requiredSpent += cost;
            var canFill = !string.IsNullOrWhiteSpace(a.SuggestedValue) &&
                          a.SuggestionConfidence is "high" or "medium";
            Add($"aspect-required:{a.Name}", FixSeverity.Blocker,
                $"“{a.Name}” is required by this category",
                canFill
                    ? $"eBay rejects the publish without it — and the app already found “{a.SuggestedValue}” {a.SuggestionSource}."
                    : "eBay rejects the publish without it.",
                AspectFieldId(a.Name), cost, canFill, a.Name);
        }

        var invalidSpent = 0;
        foreach (var a in aspects.Where(a => a.State == AspectState.InvalidValue))
        {
            var cost = Math.Min(InvalidValueCost, Math.Max(0, InvalidValueCap - invalidSpent));
            invalidSpent += cost;
            Add($"aspect-invalid:{a.Name}", FixSeverity.Blocker,
                $"“{a.Name}” — “{a.Value}” is not one of eBay's values",
                "This aspect is a fixed list; anything else is rejected at publish.",
                AspectFieldId(a.Name), cost,
                !string.IsNullOrWhiteSpace(a.SuggestedValue), a.Name);
        }

        foreach (var a in aspects.Where(a => a.State == AspectState.TooLong))
            Add($"aspect-too-long:{a.Name}", FixSeverity.Blocker,
                $"“{a.Name}” is longer than eBay allows",
                a.Note.Length > 0 ? a.Note : "eBay caps the length of this value.",
                AspectFieldId(a.Name), 6, false, a.Name);

        // ── Warnings: it lists, and earns less ────────────────────────────────────────────────

        if (title.Length > 0 && title.Length <= MaxTitleLength && title.Length < ThinTitleLength)
            Add("title-thin", FixSeverity.Warning,
                $"Title uses {title.Length} of {MaxTitleLength} characters",
                "Every word in the title is a search a buyer can find you with. The unused space is free.",
                "nl-title", 6);

        if (photos.Count is > 0 and < GoodPhotoCount)
            Add("photos-thin", FixSeverity.Warning,
                $"Only {photos.Count} photo{(photos.Count == 1 ? "" : "s")}",
                "eBay gives 24 slots free. More angles means fewer questions, fewer returns and a higher price.",
                "nl-photo-grid", 8);
        else if (photos.Count is >= GoodPhotoCount and < IdealPhotoCount)
            Add("photos-more", FixSeverity.Tip,
                $"{photos.Count} photos — {IdealPhotoCount}+ does better",
                "Serial-number and port shots settle condition disputes before they start.",
                "nl-photo-grid", 3);

        var missingRecommended = aspects.Where(a => a.State == AspectState.MissingRecommended).ToList();
        if (missingRecommended.Count > 0)
        {
            var recSpent = 0;
            foreach (var a in missingRecommended)
            {
                var cost = Math.Min(MissingRecommendedCost, Math.Max(0, MissingRecommendedCap - recSpent));
                recSpent += cost;
                var canFill = !string.IsNullOrWhiteSpace(a.SuggestedValue) &&
                              a.SuggestionConfidence is "high" or "medium";
                Add($"aspect-recommended:{a.Name}", FixSeverity.Warning,
                    $"“{a.Name}” is empty",
                    canFill
                        ? $"Buyers filter search results by this, so a blank one hides the listing from them. The app found “{a.SuggestedValue}” {a.SuggestionSource}."
                        : "Buyers filter search results by this, so a blank one hides the listing from them.",
                    AspectFieldId(a.Name), cost, canFill, a.Name);
            }
        }

        if (descriptionText.Length == 0)
            Add("description-missing", FixSeverity.Warning, "Write a description",
                "An empty description reads as a rushed or risky listing, and buyers price that in.",
                "nl-desc-text", 8);
        else if (descriptionText.Length < ThinDescriptionLength)
            Add("description-thin", FixSeverity.Tip,
                $"Description is {descriptionText.Length} characters",
                "What's included, what's been tested and what's wrong with it — that is what stops the questions and the returns.",
                "nl-desc-text", 4);

        var hasIdentifier = !string.IsNullOrWhiteSpace(listing.Upc) ||
                            !string.IsNullOrWhiteSpace(listing.Ean) ||
                            !string.IsNullOrWhiteSpace(listing.Isbn) ||
                            !string.IsNullOrWhiteSpace(listing.Mpn);
        if (!hasIdentifier)
            Add("identifiers-missing", FixSeverity.Warning, "No UPC, EAN or MPN",
                "A product identifier is how eBay matches the listing to its catalogue — that is what puts it in the product page's list of offers rather than leaving it alone in search.",
                "nl-upc", 5);

        if (string.IsNullOrWhiteSpace(listing.Brand))
            Add("brand-missing", FixSeverity.Warning, "No brand",
                "Brand is the filter buyers reach for first, and it feeds the Brand item specific below.",
                "nl-brand", 4);

        if (string.IsNullOrWhiteSpace(listing.ItemLocationPostalCode))
            Add("location-missing", FixSeverity.Tip, "No item location ZIP",
                "Buyers see delivery estimates from it, and calculated shipping needs it to quote.",
                "nl-location-zip", 3);

        if (listing.WeightLbs <= 0 && listing.WeightOz <= 0)
            Add("weight-missing", FixSeverity.Tip, "No package weight",
                "Without it, calculated shipping cannot quote and a flat rate has to absorb the worst case.",
                "nl-weight-lbs", 3);

        if (listing.Price >= 250 && !listing.BestOfferEnabled)
            Add("best-offer-off", FixSeverity.Tip, "Best Offer is off",
                "On higher-priced items it converts browsers into a negotiation you can still decline.",
                "nl-best-offer", 2);

        if (string.IsNullOrWhiteSpace(listing.ConditionDescription) &&
            !string.Equals(listing.Condition, "NEW", StringComparison.OrdinalIgnoreCase))
            Add("condition-desc-missing", FixSeverity.Tip, "No condition description",
                "On a used item this is the single line that prevents a not-as-described case.",
                "nl-condition-desc", 3);

        score = Math.Clamp(score, 0, 100);

        var blockers = fixes.Count(f => f.Severity == FixSeverity.Blocker);
        var warnings = fixes.Count(f => f.Severity == FixSeverity.Warning);

        // Blockers first, then by what they cost — the order to work through them.
        var ordered = fixes
            .OrderBy(f => f.Severity switch
            {
                FixSeverity.Blocker => 0,
                FixSeverity.Warning => 1,
                _ => 2,
            })
            .ThenByDescending(f => f.ScoreCost)
            .ThenBy(f => f.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ListingReadinessResult
        {
            Score = score,
            Grade = Grade(score, blockers),
            Headline = Headline(score, blockers, warnings, aspectStatus),
            Fixes = ordered,
            Aspects = aspects.ToList(),
            CustomAspectNames = customAspectNames?.ToList() ?? [],
            BlockerCount = blockers,
            WarningCount = warnings,
            AutoFillableCount = AspectMatcher.AutoFillable(aspects).Count,
            AspectStatus = aspectStatus,
            AspectMessage = aspectMessage,
        };
    }

    // A high score with a blocker in it is not "nearly ready" — it is not ready. Grade tracks
    // publishability first and polish second, because the two failure modes are not comparable.
    public static string Grade(int score, int blockers)
    {
        if (blockers > 0) return "Won't publish";
        return score switch
        {
            >= 90 => "Ready to list",
            >= 75 => "Good",
            >= 55 => "Needs work",
            _     => "Thin",
        };
    }

    private static string Headline(int score, int blockers, int warnings, string aspectStatus)
    {
        if (blockers > 0)
            return blockers == 1
                ? "1 thing will stop this publishing"
                : $"{blockers} things will stop this publishing";

        if (aspectStatus == "not_connected")
            return "Nothing here blocks a publish — but eBay's required specifics weren't checked";
        if (aspectStatus == "no_category")
            return "Pick a category to check what eBay requires";
        if (aspectStatus == "error")
            return "Nothing here blocks a publish — eBay's requirement check didn't answer";

        if (warnings == 0 && score >= 95) return "Ready to publish";
        if (warnings == 0) return "Clear to publish";
        return warnings == 1
            ? "Publishable — 1 thing is costing you buyers"
            : $"Publishable — {warnings} things are costing you buyers";
    }

    // Stable DOM id per aspect so a fix in the list can focus the field it belongs to. Matches
    // the id the frontend renders (see nlAspectFieldId in app.js) — the two must agree.
    public static string AspectFieldId(string aspectName)
    {
        var slug = new string((aspectName ?? "")
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return "nl-aspect-" + slug.Trim('-');
    }
}
