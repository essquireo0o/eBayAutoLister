namespace ING_eBay_AutoLister.Models;

// ── eBay's own answer to "what does this category require?" ───────────────────────────────────
//
// Everything in this file exists because eBay will not tell a seller what a listing needs until
// *after* they press Publish and it fails. The Taxonomy API knows the answer up front
// (get_item_aspects_for_category); nothing in the app was asking it. These are the shapes that
// answer travels in.

// One Item Specific as eBay defines it for a category. `Values` is eBay's own list of accepted
// values — the reason this feature can do more than nag: when eBay names the legal values, the
// app can look for them in the seller's own title and description instead of asking.
public sealed class CategoryAspect
{
    public string Name { get; set; } = "";

    // aspectRequired — eBay rejects the publish without it.
    public bool Required { get; set; }

    // aspectUsage == RECOMMENDED — not enforced, but it is what buyers filter search results by,
    // which is the difference between being findable and not.
    public bool Recommended { get; set; }

    // aspectMode == SELECTION_ONLY — a value outside `Values` is rejected even if it reads fine.
    public bool SelectionOnly { get; set; }

    // itemToAspectCardinality == MULTI — more than one value is allowed.
    public bool MultiSelect { get; set; }

    public int MaxLength { get; set; }

    // Whether eBay published the full value list or only a sample. A short sample list must not
    // be treated as exhaustive when deciding a typed value is "invalid".
    public bool ValuesAreComplete { get; set; }

    public List<string> Values { get; set; } = [];
}

public sealed class CategoryAspectsResult
{
    public string CategoryId { get; set; } = "";

    // ok | not_connected | error | no_category
    public string Status { get; set; } = "ok";
    public string Message { get; set; } = "";

    public List<CategoryAspect> Aspects { get; set; } = [];
}

// ── Per-aspect verdict, after matching the draft against eBay's list ──────────────────────────

public static class AspectState
{
    public const string Filled            = "filled";              // seller supplied a valid value
    public const string Suggested         = "suggested";           // app found a value; not applied yet
    public const string MissingRequired   = "missing_required";    // eBay will reject
    public const string MissingRecommended= "missing_recommended"; // buyers filter on it
    public const string InvalidValue      = "invalid_value";       // not in eBay's SELECTION_ONLY list
    public const string TooLong           = "too_long";
    public const string Optional          = "optional";
}

public sealed class AspectField
{
    public string Name { get; set; } = "";
    public bool Required { get; set; }
    public bool Recommended { get; set; }
    public bool SelectionOnly { get; set; }
    public bool MultiSelect { get; set; }
    public int MaxLength { get; set; }
    public List<string> Values { get; set; } = [];

    // What the seller currently has, under whatever name they typed it as.
    public string Value { get; set; } = "";

    // The name the seller used, when it differed from eBay's ("GPU Model" → "Chipset/GPU Model").
    // Shown, not hidden: a silently renamed field is a field the seller can't find again.
    public string MatchedFromKey { get; set; } = "";

    // A value the app derived and is offering. Never written without the seller applying it.
    public string SuggestedValue { get; set; } = "";

    // high | medium | low — only high/medium are applied by "Fill from my listing".
    public string SuggestionConfidence { get; set; } = "";

    // Where the suggestion came from, in plain words ("from the Brand field", "found in your title").
    public string SuggestionSource { get; set; } = "";

    public string State { get; set; } = AspectState.Optional;
    public string Note { get; set; } = "";
}

// ── Whole-listing readiness ───────────────────────────────────────────────────────────────────

public static class FixSeverity
{
    public const string Blocker = "blocker";  // eBay rejects it, or it cannot go live
    public const string Warning = "warning";  // it will list, and sell for less / be found less
    public const string Tip     = "tip";
}

public sealed class ReadinessFix
{
    public string Id { get; set; } = "";
    public string Severity { get; set; } = FixSeverity.Warning;
    public string Label { get; set; } = "";

    // Why it costs money, in one sentence. A checklist without this is just nagging.
    public string Why { get; set; } = "";

    // DOM id the UI focuses when the fix is clicked; empty when the fix has no single field.
    public string FieldId { get; set; } = "";

    // Aspect name, when the fix is about one Item Specific.
    public string AspectName { get; set; } = "";

    // True when "Fill from my listing" can resolve it without the seller typing anything.
    public bool AutoFixable { get; set; }

    public int ScoreCost { get; set; }
}

public sealed class ListingReadinessResult
{
    public int Score { get; set; }
    public string Grade { get; set; } = "";
    public string Headline { get; set; } = "";

    public List<ReadinessFix> Fixes { get; set; } = [];
    public List<AspectField> Aspects { get; set; } = [];

    // Item Specifics the seller typed that eBay has no aspect for. Kept and sent — eBay accepts
    // extra specifics — but reported so a typo'd name doesn't masquerade as a filled requirement.
    public List<string> CustomAspectNames { get; set; } = [];

    public int BlockerCount { get; set; }
    public int WarningCount { get; set; }

    // How many aspects "Fill from my listing" would populate right now.
    public int AutoFillableCount { get; set; }

    // ok | not_connected | error | no_category — the state of the eBay aspect lookup specifically.
    // Everything else in this result is still computed when this is not ok, and the UI says so
    // rather than implying the requirements were checked.
    public string AspectStatus { get; set; } = "ok";
    public string AspectMessage { get; set; } = "";
}

public sealed class ReadinessRequest : ListingData
{
    // Skip the eBay call and score everything else — used while the seller is still typing.
    public bool SkipAspects { get; set; }
}
