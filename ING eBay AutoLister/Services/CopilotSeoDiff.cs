using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Describes what a rewrite did to a listing, across the whole listing rather than the title.
/// </summary>
/// <remarks>
/// <para>
/// The Copilot panel promises the seller the full list of changes before anything is published, and
/// for a long time the SEO card showed a struck-through old title next to a new one and nothing
/// else. That is a fraction of what the pass does: the description is replaced outright and the item
/// specifics are filled in. A seller reading a title diff had no way to know the rest had happened,
/// which is the opposite of what the promise at the top of the panel says.
/// </para>
/// <para>
/// Sizes rather than bodies for the description, deliberately. The status endpoint hands back up to
/// sixty of these on every poll, and a poll that carries sixty copies of a seven-kilobyte HTML
/// description would be half a megabyte every two and a half seconds. The seller opens the draft to
/// read the description; here they only need to know it changed and by how much.
/// </para>
/// </remarks>
public static class CopilotSeoDiff
{
    /// <summary>
    /// How many individual change lines are reported per listing. A rewrite can fill thirty item
    /// specifics; past a couple of dozen lines nobody is reading them anyway, and the rest is
    /// payload on every poll.
    /// </summary>
    private const int MaxLines = 24;

    /// <summary>Compares a listing with its rewrite and says what a seller would see change.</summary>
    public static CopilotSeoChanges Describe(ListingData? before, ListingData? after)
    {
        var b = before ?? new ListingData();
        var a = after ?? new ListingData();
        var lines = new List<CopilotSeoChangeLine>();

        AddText(lines, "Title", b.Title, a.Title);
        AddText(lines, "Subtitle", b.Subtitle, a.Subtitle);

        var beforeDesc = N(b.Description);
        var afterDesc = N(a.Description);
        if (!string.Equals(beforeDesc, afterDesc, StringComparison.Ordinal))
            lines.Add(new CopilotSeoChangeLine(
                "Description",
                beforeDesc.Length == 0 ? "filled" : "changed",
                beforeDesc.Length == 0 ? "" : Chars(beforeDesc.Length),
                Chars(afterDesc.Length) + " of formatted HTML"));

        AddText(lines, "Condition description", b.ConditionDescription, a.ConditionDescription);
        AddText(lines, "Brand", b.Brand, a.Brand);
        AddText(lines, "MPN", b.Mpn, a.Mpn);

        // Item specifics last, because there are the most of them and they are the reason the
        // summary needs to expand at all.
        var bs = b.ItemSpecifics ?? [];
        var as_ = a.ItemSpecifics ?? [];

        var filled = 0;
        var corrected = 0;
        foreach (var kv in as_.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var had = bs.TryGetValue(kv.Key, out var was) && N(was).Length > 0;
            var now = N(kv.Value);
            if (now.Length == 0) continue;
            if (!had) { filled++; lines.Add(new CopilotSeoChangeLine(kv.Key, "filled", "", now)); }
            else if (!string.Equals(N(was), now, StringComparison.Ordinal))
            {
                corrected++;
                lines.Add(new CopilotSeoChangeLine(kv.Key, "changed", N(was), now));
            }
        }

        // A specific that disappears is a filter the listing drops out of, so it is reported rather
        // than quietly left off the list.
        var dropped = 0;
        foreach (var kv in bs.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (N(kv.Value).Length == 0) continue;
            if (as_.TryGetValue(kv.Key, out var still) && N(still).Length > 0) continue;
            dropped++;
            lines.Add(new CopilotSeoChangeLine(kv.Key, "removed", N(kv.Value), ""));
        }

        var headline = Headline(
            titleChanged: !string.Equals(N(b.Title), N(a.Title), StringComparison.Ordinal),
            subtitleChanged: !string.Equals(N(b.Subtitle), N(a.Subtitle), StringComparison.Ordinal),
            descriptionChanged: !string.Equals(beforeDesc, afterDesc, StringComparison.Ordinal),
            filled, corrected, dropped);

        var shown = lines.Count > MaxLines ? lines.Take(MaxLines).ToList() : lines;
        return new CopilotSeoChanges(headline, shown, lines.Count - shown.Count);
    }

    private static void AddText(List<CopilotSeoChangeLine> lines, string field, string? before, string? after)
    {
        var b = N(before);
        var a = N(after);
        if (string.Equals(b, a, StringComparison.Ordinal)) return;
        // A field the rewrite emptied is a loss, not an improvement — say so in those words rather
        // than showing a change to nothing.
        if (a.Length == 0) { lines.Add(new CopilotSeoChangeLine(field, "removed", b, "")); return; }
        lines.Add(new CopilotSeoChangeLine(field, b.Length == 0 ? "filled" : "changed", b, a));
    }

    private static string Headline(
        bool titleChanged, bool subtitleChanged, bool descriptionChanged,
        int filled, int corrected, int dropped)
    {
        var parts = new List<string>();
        if (titleChanged) parts.Add("new title");
        if (subtitleChanged) parts.Add("new subtitle");
        if (descriptionChanged) parts.Add("description rewritten");
        if (filled > 0) parts.Add(filled + (filled == 1 ? " item specific filled in" : " item specifics filled in"));
        if (corrected > 0) parts.Add(corrected + " corrected");
        if (dropped > 0) parts.Add(dropped + " removed");

        if (parts.Count == 0) return "Nothing changed";

        var text = string.Join(", ", parts);
        return char.ToUpperInvariant(text[0]) + text[1..] + ". Photos unchanged.";
    }

    private static string Chars(int n) => n.ToString("N0") + " characters";

    private static string N(string? s) => (s ?? "").Trim();
}

/// <summary>What one rewrite changed, as a line the seller can read at a glance plus the detail.</summary>
/// <param name="Headline">One scannable sentence — what the seller sees before expanding anything.</param>
/// <param name="Lines">Field-by-field detail, capped so a poll stays small.</param>
/// <param name="MoreCount">How many further lines were left off the list.</param>
public sealed record CopilotSeoChanges(
    string Headline, IReadOnlyList<CopilotSeoChangeLine> Lines, int MoreCount);

/// <param name="Kind">
/// <c>filled</c> when the field was empty and now has a value, <c>changed</c> when it had one and it
/// is different, <c>removed</c> when the rewrite emptied it. The three read very differently to a
/// seller: filling a blank is the point of the feature, and emptying one is a mistake to catch.
/// </param>
public sealed record CopilotSeoChangeLine(string Field, string Kind, string Before, string After);
