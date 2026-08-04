using System.Text;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Reads a listing title against the categories the seller has published under before, and names
/// the one they would have picked.
/// </summary>
/// <remarks>
/// <para>
/// A seller who lists forty Antminers has told the app where an Antminer goes forty times. The
/// forty-first still opened on a blank category box, a search, and a dropdown — and until that box
/// was filled the readiness check could not say what eBay required either, because required Item
/// Specifics are defined per category. One empty field was holding up the entire rest of the
/// check.
/// </para>
/// <para>
/// The matching is deliberately literal. Titles are cut into significant words, model numbers
/// count for more than adjectives, and the score is how much of the <em>new</em> title the old one
/// covers. Nothing is inferred about the item: if the words do not overlap, no category is
/// offered, because the alternative — guessing — puts the listing in the wrong search results and
/// hangs the wrong required specifics off it.
/// </para>
/// <para>
/// Pure and static: no database, no eBay call, no clock. History is passed in already read.
/// </para>
/// </remarks>
public static class CategorySuggester
{
    /// <summary>Overlap at which a match is offered as read rather than as a guess.</summary>
    public const double StrongMatch = 0.55;

    /// <summary>Below this, the two titles are not about the same kind of thing.</summary>
    public const double UsableMatch = 0.30;

    /// <summary>How far ahead of the runner-up the winner has to be to be called confident.</summary>
    public const double StrongLead = 0.15;

    /// <summary>Two categories this close together is a coin flip, and is not offered at all.</summary>
    public const double UsableLead = 0.05;

    /// <summary>Weight of shared words below which a match is coincidence — "new" and "lot" in
    /// common is not evidence that two items belong in the same aisle.</summary>
    public const int MinSharedWeight = 2;

    /// <summary>How much of a past title is quoted back on the button that accepts the match.</summary>
    public const int ExampleTitleLength = 46;

    // Words that appear in listing titles regardless of what is being sold. Every one of these is
    // a word two unrelated items can share, so counting them would make "New Sealed Lot Free
    // Shipping" match everything the seller has ever listed.
    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "from", "this", "that", "your", "you", "our", "its",
        "new", "used", "oem", "genuine", "original", "authentic", "brand", "official",
        "free", "fast", "ship", "shipping", "sale", "hot", "top", "best", "great", "deal",
        "lot", "set", "pack", "packs", "pcs", "piece", "pieces", "bundle", "kit", "box", "boxed",
        "working", "works", "tested", "sealed", "open", "excellent", "good", "mint", "nice",
        "condition", "quality", "premium", "authenticated", "refurbished", "refurb",
        "usa", "only", "not", "all", "any", "one", "two", "more", "less", "plus", "extra",
        "read", "description", "please", "see", "photos", "pics", "pictures", "look",
        "rare", "vintage", "huge", "big", "small", "large", "mini", "super", "ultra",
    };

    /// <summary>
    /// The significant words in a title, lowercased and de-duplicated in first-seen order.
    /// </summary>
    /// <remarks>
    /// Single characters go, because "x" in "16 x 9" is punctuation wearing a letter. Bare numbers
    /// under three digits go, because a quantity, a size and a year of manufacture are all "12".
    /// Everything else stays: <c>hp</c> is a brand and <c>s19</c> is the whole item.
    /// </remarks>
    public static List<string> Tokens(string? title)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var outp = new List<string>();
        var buffer = new StringBuilder();

        void Flush()
        {
            if (buffer.Length == 0) return;
            var token = buffer.ToString();
            buffer.Clear();
            if (token.Length < 2) return;
            if (Noise.Contains(token)) return;
            if (token.All(char.IsDigit) && token.Length < 3) return;
            if (seen.Add(token)) outp.Add(token);
        }

        foreach (var c in title ?? "")
        {
            if (char.IsLetterOrDigit(c)) buffer.Append(char.ToLowerInvariant(c));
            else Flush();
        }
        Flush();

        return outp;
    }

    /// <summary>
    /// How much one shared word is worth as evidence that two listings belong in the same category.
    /// </summary>
    /// <remarks>
    /// A word carrying both letters and digits is a model number — <c>s19j</c>, <c>rtx4090</c>,
    /// <c>ka3</c> — and two titles sharing one are almost always the same product line. A bare
    /// number is a capacity or a size: real evidence, but weaker. A plain word is a word.
    /// </remarks>
    public static int Weight(string token)
    {
        if (string.IsNullOrEmpty(token)) return 0;
        var hasDigit  = token.Any(char.IsDigit);
        var hasLetter = token.Any(char.IsLetter);
        if (hasDigit && hasLetter) return 3;
        if (hasDigit) return 2;
        return 1;
    }

    /// <summary>
    /// A stable key for "titles that describe the same thing", used to collapse repeat listings
    /// onto one remembered row instead of storing the same lesson forty times.
    /// </summary>
    public static string Key(string? title)
    {
        var tokens = Tokens(title);
        tokens.Sort(StringComparer.Ordinal);
        return string.Join(" ", tokens);
    }

    /// <summary>
    /// How much of <paramref name="title"/> is covered by <paramref name="pastTitle"/>, and the
    /// weight of the words they share.
    /// </summary>
    /// <remarks>
    /// Coverage is measured against the new title on purpose. A past title with twenty words in it
    /// should not score badly for containing detail the new one lacks — what matters is whether
    /// the thing being written about now is a thing the seller has already placed.
    /// </remarks>
    public static (double Score, int SharedWeight) Overlap(string? title, string? pastTitle)
    {
        var mine  = Tokens(title);
        var theirs = new HashSet<string>(Tokens(pastTitle), StringComparer.Ordinal);
        if (mine.Count == 0 || theirs.Count == 0) return (0, 0);

        var total = 0;
        var shared = 0;
        foreach (var token in mine)
        {
            var w = Weight(token);
            total += w;
            if (theirs.Contains(token)) shared += w;
        }

        return total == 0 ? (0, 0) : ((double)shared / total, shared);
    }

    /// <summary>
    /// The category the seller has used for titles like this one, or null when their history does
    /// not say.
    /// </summary>
    /// <remarks>
    /// Returning null is the normal answer for a seller listing something new, and it costs
    /// nothing — the form behaves exactly as it did before. A wrong answer costs a listing in the
    /// wrong aisle, so every close call here resolves to silence.
    /// </remarks>
    public static CategoryMatch? FromHistory(string? title, IEnumerable<CategoryUse>? history)
    {
        if (string.IsNullOrWhiteSpace(title) || history is null) return null;

        // Best row per category: a seller who has published this category twenty times should not
        // outrank a closer match purely on volume, so the score is the closest title, not the sum.
        var byCategory = new Dictionary<string, CategoryMatch>(StringComparer.Ordinal);

        foreach (var use in history)
        {
            if (use is null || string.IsNullOrWhiteSpace(use.CategoryId)) continue;

            var (score, sharedWeight) = Overlap(title, use.Title);
            if (score < UsableMatch || sharedWeight < MinSharedWeight) continue;

            if (byCategory.TryGetValue(use.CategoryId, out var existing))
            {
                existing.TimesUsed += Math.Max(1, use.UseCount);
                // A better-named row is worth keeping even when it is not the closest match: the
                // ID publishes, but the name is what the seller reads on the chip.
                if (string.IsNullOrWhiteSpace(existing.CategoryName) && !string.IsNullOrWhiteSpace(use.CategoryName))
                    existing.CategoryName = use.CategoryName;
                if (score <= existing.Score) continue;
                existing.Score = score;
                existing.ExampleTitle = use.Title ?? "";
                if (!string.IsNullOrWhiteSpace(use.CategoryName)) existing.CategoryName = use.CategoryName;
                continue;
            }

            byCategory[use.CategoryId] = new CategoryMatch
            {
                CategoryId = use.CategoryId,
                CategoryName = use.CategoryName ?? "",
                Score = score,
                TimesUsed = Math.Max(1, use.UseCount),
                ExampleTitle = use.Title ?? "",
            };
        }

        if (byCategory.Count == 0) return null;

        var ranked = byCategory.Values
            .OrderByDescending(m => m.Score)
            .ThenByDescending(m => m.TimesUsed)
            .ThenBy(m => m.CategoryId, StringComparer.Ordinal)
            .ToList();

        var top  = ranked[0];
        var lead = ranked.Count == 1 ? top.Score : top.Score - ranked[1].Score;

        // Two categories neck and neck means the seller themselves has filed items like this both
        // ways. Picking one for them would be the app inventing a decision they never made.
        if (lead < UsableLead) return null;

        top.Confidence = top.Score >= StrongMatch && lead >= StrongLead
            ? ListingAutofill.High
            : ListingAutofill.Medium;
        top.Source = SourceSentence(top);
        return top;
    }

    /// <summary>
    /// eBay's own answer, wrapped in the same shape so the form treats both sources alike.
    /// </summary>
    /// <remarks>
    /// Offered at medium, never high. eBay is guessing from the title too, and it is guessing
    /// without knowing what the seller sells — the seller's own history, when it has an opinion,
    /// outranks it. This is the answer for the first listing of something, which is exactly when
    /// there is no history to read.
    /// </remarks>
    public static CategoryMatch? FromEbay(string? categoryId, string? categoryName, string? breadcrumb = null)
    {
        if (string.IsNullOrWhiteSpace(categoryId)) return null;
        return new CategoryMatch
        {
            CategoryId = categoryId.Trim(),
            CategoryName = (categoryName ?? "").Trim(),
            Confidence = ListingAutofill.Medium,
            Source = string.IsNullOrWhiteSpace(breadcrumb) || breadcrumb is "leaf" or "parent"
                ? "eBay’s own suggestion for this title"
                : "eBay’s own suggestion — " + breadcrumb.Trim(),
            Score = 0,
            TimesUsed = 0,
        };
    }

    private static string SourceSentence(CategoryMatch match)
    {
        var example = Clip(match.ExampleTitle);
        if (match.TimesUsed > 1)
            return $"where you put {match.TimesUsed} listings like this, including “{example}”";
        return $"where you put “{example}”";
    }

    private static string Clip(string? title)
    {
        var text = (title ?? "").Trim();
        if (text.Length <= ExampleTitleLength) return text;
        var cut = text[..ExampleTitleLength];
        var space = cut.LastIndexOf(' ');
        if (space > ExampleTitleLength / 2) cut = cut[..space];
        return cut.TrimEnd() + "…";
    }
}
