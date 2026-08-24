using System.Text;
using System.Text.RegularExpressions;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// What the sold search actually asks eBay for, when the seller types what a live host just said.
/// </summary>
/// <remarks>
/// <para>
/// Every number on the live card stands on one thing: that the comp lookup found sales of the thing
/// on screen. The lookup is a keyword search against the hosted sold-comps database, and that search
/// is a <b>boolean AND</b> — every word in the query has to appear in a sold title. Which makes the
/// name a live show puts on a lot the single most expensive input in this feature:
/// </para>
/// <code>
///   🔥3x Bitmain Antminer S9 13.5TH — NO RESERVE!! ships free 📦
/// </code>
/// <para>
/// No eBay listing in history contains the words "NO RESERVE" and "ships free" and "3x" and
/// "Antminer". So the search returns nothing, the card says <b>CAN'T PRICE IT</b>, and the seller —
/// who has about eight seconds — reads that as "this item has no market". It has a market. The
/// question was unanswerable.
/// </para>
/// <para>
/// This strips the words that describe the <b>sale</b> and keeps every word that describes the
/// <b>item</b>. What it drops is shown on the card, struck through, with the reason, and one button
/// puts the typed name back — because a screen that quietly edits what you asked for is a screen you
/// cannot check.
/// </para>
///
/// <para><b>The asymmetry it is built around.</b> The two mistakes cost very different things:</para>
/// <list type="table">
///   <item>
///     <term>Dropping a word that mattered</term>
///     <description>The comps are for a slightly different thing and the price is quietly wrong.
///     Nothing on screen says so.</description>
///   </item>
///   <item>
///     <term>Keeping a word that didn't</term>
///     <description>The search returns nothing and the card says CAN'T PRICE IT — loudly, where the
///     seller can see it and press the button.</description>
///   </item>
/// </list>
/// <para>
/// The second failure is the visible one, so this drops <b>conservatively</b>: only wording that
/// cannot describe an item at all. Condition, completeness and authenticity words are never
/// touched — <i>sealed</i>, <i>graded</i>, <i>for parts</i>, <i>tested</i>, <i>NIB</i> and
/// <i>vintage</i> are what decide which end of the price spread a thing lands on, and a search that
/// dropped them would compare a sealed box to an opened one and call the difference profit.
/// </para>
/// <para>Pure. Nothing here prices anything, reads a network or holds state.</para>
/// </remarks>
public static partial class LiveSearchQuery
{
    /// <summary>
    /// The fewest identifying words a query may be cut down to. Below this the cleaning is refused
    /// whole and the typed name is searched exactly as written: a one-word query against a sold
    /// database is not a search, it is a category.
    /// </summary>
    public const int MinImportantWords = 2;

    /// <summary>
    /// How many identifying words <see cref="Widen"/> keeps. Three is the brand, the model and the
    /// one spec that separates two models — the shape of nearly every eBay title that matters.
    /// </summary>
    public const int WidenToWords = 3;

    // ── The lot's own count ──────────────────────────────────────────────────────────────────
    // "Lot 12:" and "Item #4" are where the thing sits in tonight's running order. They are the one
    // piece of a live lot name that is about the SHOW rather than about the item or the sale.
    [GeneratedRegex(@"\b(?:lots?|items?)\s*#?\s*\d{1,4}\b(?!\s*(?:th|gb|tb|mm|in)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex LotNumberRegex();

    /// <summary>
    /// Bulk wording with no number in it. Dropped because it describes the <i>packaging of the
    /// sale</i>, and because <see cref="LiveLotSize"/> has already turned it into a question on the
    /// card — "set of" goes, and a bare "set" stays, since a chess set and a drum set are products.
    /// </summary>
    [GeneratedRegex(@"\b(?:lots?\s+of|sets?\s+of|bundles?\s+of|lots?|bundle|grab\s+bag)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BulkWordingRegex();

    /// <summary>
    /// Auction and show talk. Every phrase here is about the selling of the thing, never about the
    /// thing.
    /// </summary>
    /// <remarks>
    /// Two words are deliberately absent, and both were removed after asking what they name on a
    /// live show: <c>hot</c> (Hot Wheels is the second-biggest category on Whatnot) and <c>fire</c>
    /// (Amazon Fire, Fire Emblem). The 🔥 that means the same thing is a symbol and is dropped as
    /// decoration; the word is a product name and is kept.
    /// </remarks>
    [GeneratedRegex(@"\b(?:no\s+reserve|reserve\s+met|opening\s+bid|start(?:ing|s)?\s+(?:at|bid)"
        + @"|going\s+once|going\s+twice|first\s+up|next\s+up|up\s+next|last\s+call|sold\s+out"
        + @"|must\s+see|must\s+go|do\s*n(?:'|)t\s+miss|do\s+not\s+miss|act\s+fast|hot\s+deal"
        + @"|deal\s+of\s+the\s+(?:night|day)|price\s+drop|giveaway|free\s+gift|l@@k"
        + @"|wow|hurry|insane|steal|bargain|bid\s+now|bidding|whatnot|whatsnot)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AuctionChatterRegex();

    /// <summary>
    /// Shipping, returns and payment terms. What it costs to get the thing to you is a fact about
    /// the seller, and no sold eBay title has ever carried it.
    /// </summary>
    [GeneratedRegex(@"\b(?:free\s+ship(?:ping)?|ships?\s+free|fast\s+ship(?:ping)?|ship(?:ping)?\s+included"
        + @"|combined\s+ship(?:ping)?|buyer\s+pays\s+ship(?:ping)?|ships?\s+(?:today|same\s+day|worldwide)"
        + @"|free\s+returns|no\s+returns)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LogisticsRegex();

    /// <summary>A price is what somebody is asking, never what the thing is. "$1 start", "$45".</summary>
    [GeneratedRegex(@"(?<![\w.])\$\s?\d[\d,]*(?:\.\d+)?", RegexOptions.IgnoreCase)]
    private static partial Regex PriceTokenRegex();

    /// <summary>A handle is the person selling it.</summary>
    [GeneratedRegex(@"(?<![\w])@\w+")]
    private static partial Regex HandleRegex();

    /// <summary>
    /// What the sold search should run on, and everything that was taken out of it to get there.
    /// </summary>
    /// <param name="typedName">The lot's name exactly as the seller typed or pasted it.</param>
    public static LiveSearchTerms Build(string? typedName)
    {
        var typed = (typedName ?? "").Trim();
        var terms = new LiveSearchTerms { Typed = typed, Query = typed };
        if (typed.Length == 0) return terms;

        var working = typed;
        var drops = new List<LiveSearchDrop>();

        working = Cut(working, LotNumberRegex(), LiveSearchDropKinds.Count,
            "the lot's number in tonight's running order", drops);

        // The count comes off the reader that PRICES it, so the words the ceiling multiplied by
        // three are exactly the words the search stops asking eBay for. Read off the name alone
        // (never the quantity box) — the name is what the search runs on.
        var units = LiveLotSize.Read(typed, null);
        if (units.Count > 1 && units.Source == LiveLotSize.SourceTitle && units.Evidence.Length > 0)
        {
            working = CutText(working, units.Evidence, LiveSearchDropKinds.Count,
                $"the count — the ceiling is already for all {units.Count}", drops);
        }

        working = Cut(working, BulkWordingRegex(), LiveSearchDropKinds.Count,
            "bulk wording — sold comps are for one of the thing", drops);
        working = Cut(working, AuctionChatterRegex(), LiveSearchDropKinds.Chatter,
            "auction talk — it is about the sale, not the item", drops);
        working = Cut(working, LogisticsRegex(), LiveSearchDropKinds.Logistics,
            "shipping terms — no sold listing's title carries them", drops);
        working = Cut(working, PriceTokenRegex(), LiveSearchDropKinds.Chatter,
            "a price — what somebody is asking is not what the thing is", drops);
        working = Cut(working, HandleRegex(), LiveSearchDropKinds.Chatter,
            "the seller's handle", drops);

        working = Undecorate(working, drops);
        var cleaned = Tidy(working);

        // The refusal. A name cleaned down to one word is not a narrower search, it is a search for
        // a whole category — and the card would then be pricing "Antminer" rather than an S19j Pro.
        if (Important(cleaned).Count < MinImportantWords && Important(typed).Count >= MinImportantWords)
        {
            terms.Refused = "Taking the auction wording out of this name would have left almost nothing to "
                + "search on, so eBay was asked for it exactly as typed.";
            terms.Note = "Searched for exactly what you typed.";
            return terms;
        }

        if (cleaned.Length == 0) return terms;

        terms.Query = cleaned;
        terms.Dropped = drops;
        terms.Note = drops.Count == 0
            ? "Searched for exactly what you typed."
            : "The sold search is a boolean AND — every word has to appear in a sold listing's title, "
              + "so the wording that is about the sale rather than the item was left out.";
        terms.SimilarQueries = SimilarLookupQueries(terms);

        return terms;
    }

    /// <summary>
    /// The same name, searched exactly as it was typed. The undo for a cleaning that took a word
    /// the seller wanted — offered on the card whenever anything was dropped.
    /// </summary>
    public static LiveSearchTerms Exact(string? typedName)
    {
        var typed = (typedName ?? "").Trim();
        return new LiveSearchTerms
        {
            Typed = typed,
            Query = typed,
            Note = typed.Length == 0 ? "" : "Searched for exactly what you typed, on your instruction.",
            AskedForExactly = true,
        };
    }

    /// <summary>
    /// The shorter search to try when the first one came back with nothing worth pricing on, or
    /// null when there is no shorter search that is still a search.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a real trade and it is made in one direction only: <b>evidence for precision</b>.
    /// "Pokemon 151 Ultra Premium Collection sealed English 2024" matches no sold title as a whole
    /// and its first three identifying words match hundreds, so a card that would have said CAN'T
    /// PRICE IT says a price instead — and has to say, on the card, that it widened. The app's own
    /// evidence grading does the rest: comps that no longer carry the model token come back
    /// identity-unverified, which is what turns the badge amber.
    /// </para>
    /// <para>
    /// It keeps the <i>leading</i> words because that is where a live host puts the brand and the
    /// model, and it cuts the original text rather than rebuilding it, so nothing is reordered and
    /// no word appears in the query that the seller did not type.
    /// </para>
    /// </remarks>
    public static LiveSearchTerms? Widen(LiveSearchTerms terms) => WidenTo(terms, WidenToWords);

    /// <summary>
    /// Every rung of the widening, broadest last: the whole name minus one identifying word, then
    /// minus two, down to <see cref="MinImportantWords"/>. The caller walks it and stops at the
    /// first rung with enough sold history to price on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a ladder and not the one jump.</b> <see cref="Widen"/> goes straight to three words,
    /// which for "1884 CC Morgan Silver Dollar GSA Holder Uncirculated Carson City" is
    /// "1884 CC Morgan" — and steps clean over "1884 CC Morgan Silver Dollar", which is the exact
    /// title that coin sells under on eBay every week. Jumping to the bottom of the ladder finds
    /// either nothing or the wrong thing; walking down it finds the closest sold history there is,
    /// which is what a price is supposed to be made of (owner, 2026-08-21: "the sold comps need to
    /// look harder … it should find the closest one").
    /// </para>
    /// <para>
    /// Each rung is still a cut of the seller's own words, in their own order — nothing is added,
    /// nothing is reordered, and every rung says on the card what it gave up to find a price.
    /// </para>
    /// </remarks>
    public static IEnumerable<LiveSearchTerms> Ladder(LiveSearchTerms terms)
    {
        var count = Important((terms.Query ?? "").Trim()).Count;
        for (var keep = count - 1; keep >= MinImportantWords; keep--)
            if (WidenTo(terms, keep) is { } rung)
                yield return rung;
    }

    /// <summary>
    /// A bounded live-search ladder: two progressively closer cuts, the three-word product core,
    /// then the two-word floor. This is wide enough to reach “1955 Washington Quarter” from a
    /// grading-detail title without spending one external lookup for every word in that title.
    /// </summary>
    public static List<string> SimilarLookupQueries(LiveSearchTerms terms)
    {
        var count = Important((terms.Query ?? "").Trim()).Count;
        if (count <= MinImportantWords) return [];

        var targets = new[]
        {
            count - 2,
            Math.Max(WidenToWords, (int)Math.Ceiling(count / 2m)),
            WidenToWords,
            MinImportantWords,
        };

        return targets
            .Where(keep => keep >= MinImportantWords && keep < count)
            .Distinct()
            .OrderByDescending(keep => keep)
            .Select(keep => WidenTo(terms, keep)?.Query)
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
    }

    /// <summary>The widening itself, cut to <paramref name="keepWords"/> identifying words.</summary>
    private static LiveSearchTerms? WidenTo(LiveSearchTerms terms, int keepWords)
    {
        var query = (terms.Query ?? "").Trim();
        if (query.Length == 0 || terms.Widened) return null;
        if (keepWords < MinImportantWords) return null;
        if (Important(query).Count <= keepWords) return null;

        var kept = new StringBuilder();
        var seen = 0;
        foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seen >= keepWords) break;
            if (kept.Length > 0) kept.Append(' ');
            kept.Append(word);
            if (Important(word).Count > 0) seen++;
        }

        var shorter = kept.ToString();
        if (shorter.Length == 0 || shorter.Length >= query.Length) return null;

        var tail = query[shorter.Length..].Trim();
        var wider = new LiveSearchTerms
        {
            Typed = terms.Typed,
            Query = shorter,
            Dropped = [.. terms.Dropped],
            Widened = true,
            AskedForExactly = terms.AskedForExactly,
            Note = terms.Note,
            // If stored history improved on one rung but still did not reach the minimum comp
            // count, the live fallback must keep the rest of the ladder. Otherwise one thin stored
            // match would collapse the live search back to a single query.
            SimilarQueries = terms.SimilarQueries
                .Where(query => !string.Equals(query, shorter, StringComparison.OrdinalIgnoreCase))
                .ToList(),
            WidenedNote = $"Nothing on eBay has sold under the whole name, so the search was widened to "
                + $"“{shorter}”.",
        };

        if (tail.Length > 0)
        {
            wider.Dropped.Add(new LiveSearchDrop
            {
                Text = tail,
                Kind = LiveSearchDropKinds.Widened,
                Why = "dropped to find any sold history at all",
            });
        }

        return wider;
    }

    /// <summary>
    /// What a widened card has to say out loud, or empty when it was not widened. Lives here, next
    /// to the decision, so the sentence cannot drift from the thing it describes.
    /// </summary>
    public static string WidenedWarning(LiveSearchTerms terms) =>
        !terms.Widened ? ""
            : $"These comps are for “{terms.Query}”, not for the whole name on screen — the full "
              + "name matched nothing that has sold. They are the right ballpark and not necessarily the "
              + "right configuration, so read them before you trust the ceiling.";

    // ── The cutting ──────────────────────────────────────────────────────────────────────────

    private static string Cut(string text, Regex pattern, string kind, string why, List<LiveSearchDrop> drops)
    {
        var matches = pattern.Matches(text);
        if (matches.Count == 0) return text;

        foreach (Match m in matches)
        {
            var found = m.Value.Trim();
            if (found.Length > 0) drops.Add(new LiveSearchDrop { Text = found, Kind = kind, Why = why });
        }

        return pattern.Replace(text, " ");
    }

    private static string CutText(string text, string needle, string kind, string why, List<LiveSearchDrop> drops)
    {
        var at = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return text;

        drops.Add(new LiveSearchDrop { Text = needle, Kind = kind, Why = why });
        return string.Concat(text[..at], " ", text[(at + needle.Length)..]);
    }

    /// <summary>
    /// Emoji, bullets and exclamation runs. A live seller's name is typed one-handed between lots
    /// and decorated to be seen from across a feed; none of it survives a database query, and it is
    /// counted as one drop rather than eleven so the card does not become a wall of chips.
    /// </summary>
    private static string Undecorate(string text, List<LiveSearchDrop> drops)
    {
        var kept = new StringBuilder(text.Length);
        var removed = new List<string>();

        // Walked as text ELEMENTS rather than chars, because an emoji is two chars and half of one
        // is not a character at all. Chipping "🔥📦" apart by char and de-duplicating the halves is
        // how a "what was dropped" line ends up showing the seller a box glyph.
        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;

            // Kept: letters, digits, spaces, and the three marks that live INSIDE identifiers —
            // "13.5TH", "S19-Pro", "1/2 inch". Everything else is punctuation somebody added to be
            // noticed, and an AND search cannot see past it.
            if (element.All(c => char.IsLetterOrDigit(c) || c == ' ' || c == '.' || c == '-' || c == '/' || c == '\''))
            {
                kept.Append(element);
                continue;
            }

            kept.Append(' ');
            if (!string.IsNullOrWhiteSpace(element) && !removed.Contains(element, StringComparer.Ordinal))
                removed.Add(element);
        }

        if (removed.Count > 0)
        {
            drops.Add(new LiveSearchDrop
            {
                Text = string.Concat(removed),
                Kind = LiveSearchDropKinds.Decoration,
                Why = "emoji and punctuation — a sold-title search cannot see past them",
            });
        }

        return kept.ToString();
    }

    /// <summary>Collapses the holes the cutting left, and the separators that are now dangling.</summary>
    private static string Tidy(string text) =>
        string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim('-', '.', '/', '\''))
                .Where(w => w.Length > 0));

    private static List<string> Important(string text) =>
        MarketplaceMatcher.ImportantWords(MarketplaceMatcher.Normalize(text));
}
