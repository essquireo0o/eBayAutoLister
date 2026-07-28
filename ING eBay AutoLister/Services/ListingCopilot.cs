using System.Globalization;
using System.Text.RegularExpressions;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The analysis behind the Listing Copilot screen: what is wrong across a whole eBay account,
/// and exactly what would change if the seller said yes.
/// </summary>
/// <remarks>
/// <para>
/// Every method here is a pure function over data already fetched. Nothing in this file calls
/// eBay, writes a listing, or renames a policy. That split is the point: a bulk action across a
/// live store is only safe if the seller can read the full list of proposed changes first, and a
/// plan that cannot touch anything is a plan that can be shown without risk.
/// </para>
/// <para>
/// The apply step is deliberately somewhere else, works off the plan objects produced here, and
/// for listing text produces eBay <em>drafts</em> rather than live revisions — an SEO rewrite is a
/// judgement call, and judgement calls belong to the seller.
/// </para>
/// </remarks>
public static class ListingCopilot
{
    // eBay's hard title limit. Titles are the single biggest search-ranking lever a seller
    // controls, and unused characters are unused keywords.
    public const int MaxTitleLength = 80;

    // Below this, a title is leaving so much of the budget unused it is worth flagging on its own.
    public const int ShortTitleLength = 60;

    /// <summary>Words that eat title budget without ever being searched for.</summary>
    private static readonly string[] FillerWords =
    [
        "l@@k", "look", "wow", "rare", "hot", "nice", "great", "best", "top", "new listing",
        "free shipping", "fast shipping", "must see", "!!!", "$$$", "read", "nr", "no reserve",
    ];

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex ShoutedRun = new(@"\b[A-Z]{4,}\b", RegexOptions.Compiled);

    // ── Titles / SEO ──────────────────────────────────────────────────────────

    /// <summary>
    /// Everything wrong with one listing's title, worst first. Empty means leave it alone —
    /// a copilot that proposes a change to every row teaches the seller to click Apply blindly.
    /// </summary>
    public static IReadOnlyList<CopilotIssue> AuditTitle(string? title)
    {
        var issues = new List<CopilotIssue>();
        var t = (title ?? "").Trim();

        if (t.Length == 0)
        {
            issues.Add(new CopilotIssue("title_missing", "No title", "eBay will not surface a listing with no title."));
            return issues;
        }

        if (t.Length > MaxTitleLength)
            issues.Add(new CopilotIssue("title_too_long",
                $"Title is {t.Length} characters, {t.Length - MaxTitleLength} over eBay's limit",
                "eBay truncates it, so the tail keywords never index."));

        if (t.Length < ShortTitleLength)
            issues.Add(new CopilotIssue("title_short",
                $"Only {t.Length} of {MaxTitleLength} characters used",
                $"{MaxTitleLength - t.Length} characters of free keyword space are going unused."));

        var filler = FillerWords
            .Where(f => t.Contains(f, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (filler.Count > 0)
            issues.Add(new CopilotIssue("title_filler",
                "Contains filler: " + string.Join(", ", filler),
                "Nobody searches these words. They are spending title budget that keywords could use."));

        // ALL CAPS runs read as shouting to buyers and carry no ranking benefit — eBay's search is
        // case-insensitive, so the capitals buy nothing and cost readability.
        var shouted = ShoutedRun.Matches(t).Count;
        if (shouted >= 3)
            issues.Add(new CopilotIssue("title_shouting",
                "Mostly capitals",
                "eBay search is case-insensitive, so capitals add no ranking and hurt readability."));

        if (t.Contains("  "))
            issues.Add(new CopilotIssue("title_spacing", "Double spaces",
                "Wasted characters inside the 80-character budget."));

        return issues;
    }

    /// <summary>
    /// A tidied version of a title: filler removed, spacing normalised, truncated on a word
    /// boundary rather than mid-word. This is the deterministic pass — the AI rewrite is a
    /// separate, opt-in step, because only the seller knows the item.
    /// </summary>
    public static string TidyTitle(string? title)
    {
        var t = (title ?? "").Trim();
        if (t.Length == 0) return "";

        foreach (var f in FillerWords)
            t = Regex.Replace(t, Regex.Escape(f), " ", RegexOptions.IgnoreCase);

        t = Whitespace.Replace(t, " ").Trim();

        if (t.Length <= MaxTitleLength) return t;

        // Cut on a word boundary. A title ending mid-word looks broken and the fragment is not a
        // keyword anyone searches.
        var cut = t[..MaxTitleLength];
        var lastSpace = cut.LastIndexOf(' ');
        return (lastSpace > 40 ? cut[..lastSpace] : cut).Trim();
    }

    // ── Categories ────────────────────────────────────────────────────────────

    /// <summary>
    /// Flags listings whose category will cost them views. eBay's own taxonomy cannot be renamed
    /// or reordered by a seller — only which category a listing sits in is under the seller's
    /// control, so that is what this checks.
    /// </summary>
    public static IReadOnlyList<CopilotIssue> AuditCategory(string? categoryId, string? categoryName)
    {
        var issues = new List<CopilotIssue>();
        var name = (categoryName ?? "").Trim();
        var id = (categoryId ?? "").Trim();

        // Blank id AND blank name means the data was never fetched, not that the listing is
        // uncategorised — eBay's bulk listing call does not return the category at all. Reporting
        // "no category" here would invent a fault on every listing in the account, which is worse
        // than reporting nothing: it is confident and wrong.
        if (id.Length == 0 && name.Length == 0)
        {
            issues.Add(new CopilotIssue("category_unknown", "Category not loaded",
                "eBay's bulk listing call omits the category, so this listing needs a per-item lookup before it can be judged."));
            return issues;
        }

        if (id.Length == 0)
        {
            issues.Add(new CopilotIssue("category_missing", "No category",
                "An uncategorised listing is invisible to anyone browsing."));
            return issues;
        }

        // A leaf category is where buyers actually filter. These catch-alls are where listings go
        // to be ignored, and eBay will not show item-specific filters for them.
        if (name.Contains("Other", StringComparison.OrdinalIgnoreCase))
            issues.Add(new CopilotIssue("category_catchall",
                $"Sitting in a catch-all category ({name})",
                "\"Other\" categories carry no buyer filters, so the listing misses refined searches."));

        return issues;
    }

    // ── Business policies ─────────────────────────────────────────────────────

    /// <summary>
    /// A consistent, sortable name for a business policy, derived from the name eBay already has.
    /// </summary>
    /// <remarks>
    /// Sellers accumulate policies named by whatever they were thinking that day — "ship", "Ship
    /// 2", "FREE SHIP!!", "copy of ship 2". The dropdown is alphabetical, so near-duplicates
    /// scatter and the wrong one gets picked at listing time. Canonicalising gives one predictable
    /// shape; sorting then groups the related ones together.
    /// </remarks>
    public static string CanonicalPolicyName(string? raw)
    {
        var n = (raw ?? "").Trim();
        if (n.Length == 0) return "Unnamed";

        n = Regex.Replace(n, @"^(copy of\s+)+", "", RegexOptions.IgnoreCase);
        n = Regex.Replace(n, @"[!]+", "", RegexOptions.IgnoreCase);
        n = Whitespace.Replace(n, " ").Trim();

        // Only a PARENTHESISED trailing number counts as a duplicate marker. Matching bare
        // trailing digits looked tidier and was wrong against the real account: shipping policies
        // are named after their prices, so "…+ Intl $329.99" split into "…$329." + "99" and
        // "…Intl $350" into "…$" + "350". A rename that mangles a price is worse than no rename.
        // "(Lot 5)" is left alone for the same reason - the digits are part of the name.
        // One or two digits only. eBay writes real policy identifiers into names - "30 days money
        // back (280206111018)" - and flattening that bracket turns an ID into loose digits.
        var dupe = Regex.Match(n, @"^(?<base>.+?)\s*\((?<num>\d{1,2})\)$");
        var baseName = dupe.Success ? dupe.Groups["base"].Value.Trim() : n;
        var suffix = dupe.Success ? " " + dupe.Groups["num"].Value : "";

        baseName = ToTitleCase(baseName);
        return (baseName + suffix).Trim();
    }

    /// <summary>
    /// Shipping names are full of acronyms that are not words. Lowercasing before title-casing is
    /// what makes "FREE SHIP!!" into "Free Ship", but the same step turns "UPS Ground" into "Ups
    /// Ground" — a rename that leaves the account worse than it started. These keep their shape.
    /// </summary>
    private static readonly Dictionary<string, string> Acronyms =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ups"] = "UPS",     ["usps"] = "USPS",   ["fedex"] = "FedEx", ["dhl"] = "DHL",
            ["ems"] = "EMS",     ["ltl"] = "LTL",     ["us"] = "US",       ["usa"] = "USA",
            ["po"] = "PO",       ["asap"] = "ASAP",   ["oz"] = "oz",       ["lb"] = "lb",
            ["lbs"] = "lbs",     ["dhx"] = "DHX",     ["gnd"] = "GND",
        };

    /// <summary>Small words that stay lowercase inside a name, never at the start.</summary>
    private static readonly HashSet<string> SmallWords =
        new(StringComparer.OrdinalIgnoreCase)
        { "of", "the", "and", "a", "an", "for", "to", "in", "on", "with", "at", "by", "or", "per" };

    private static string ToTitleCase(string s)
    {
        var ti = CultureInfo.GetCultureInfo("en-US").TextInfo;
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return string.Join(' ', words.Select((w, i) =>
        {
            // Strip punctuation for the lookup so "(UPS" and "UPS," still match.
            var core = w.Trim('(', ')', ',', '.', '-', '_', ':');
            if (core.Length > 0 && Acronyms.TryGetValue(core, out var fixedForm))
                return w.Replace(core, fixedForm, StringComparison.OrdinalIgnoreCase);

            // A word that already mixes upper and lower case was capitalised on purpose:
            // SurePost, PayPal, iPhone, eBay. Re-casing those is not tidying, it is damage -
            // measured against the real account, this step was turning "UPS SurePost" into
            // "UPS Surepost" and "PayPal123" into "Paypal123".
            if (core.Any(char.IsUpper) && core.Any(char.IsLower)) return w;

            if (i > 0 && SmallWords.Contains(core)) return w.ToLowerInvariant();

            return ti.ToTitleCase(w.ToLowerInvariant());
        }));
    }

    /// <summary>
    /// Renaming plan for a set of policies: only the ones whose name would actually change, in
    /// the order they would appear afterwards. Collisions are numbered rather than dropped, so
    /// two policies can never be given the same name.
    /// </summary>
    public static IReadOnlyList<PolicyRename> PlanPolicyRenames(IEnumerable<PolicyInfo> policies)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = new List<PolicyRename>();

        foreach (var p in policies.OrderBy(p => CanonicalPolicyName(p.Name), StringComparer.OrdinalIgnoreCase))
        {
            var proposed = CanonicalPolicyName(p.Name);

            // eBay rejects duplicate policy names outright, so a plan that produced one would fail
            // halfway through and leave the account half-renamed.
            if (!taken.Add(proposed))
            {
                var n = 2;
                while (!taken.Add($"{proposed} {n}")) n++;
                proposed = $"{proposed} {n}";
            }

            if (!string.Equals(proposed, p.Name, StringComparison.Ordinal))
                plan.Add(new PolicyRename(p.Id, p.Name, proposed));
        }

        return plan;
    }
}

/// <summary>One thing wrong with one listing, in the seller's terms.</summary>
/// <param name="Code">Stable identifier, for grouping and for tests.</param>
/// <param name="Summary">What is wrong.</param>
/// <param name="WhyItCosts">Why it costs the seller money or views.</param>
public sealed record CopilotIssue(string Code, string Summary, string WhyItCosts);

/// <summary>A single proposed policy rename. Nothing is applied until the seller says so.</summary>
public sealed record PolicyRename(string PolicyId, string CurrentName, string ProposedName);
