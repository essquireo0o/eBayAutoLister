using System.Text.RegularExpressions;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

// The words the seller is competing against, counted.
//
// Everything else in this app that improves a title works from the seller's own listing — their
// words, tidied. That can only ever rearrange what is already there, and the reason a listing gets
// no views is almost always a word that was never in it. eBay's title is 80 characters of the only
// free keyword space a seller has, and the average one here uses under half of it.
//
// So this reads the market instead. Two corpora, both real and both named in the answer:
//
//   RANKED  — the titles eBay's own Best Match puts at the top of the search this item is in.
//             That is not a guess about the algorithm; it is the algorithm's output. A word in
//             31 of the top 50 is a word eBay is currently rewarding for this search.
//   SOLD    — the titles of listings that actually sold. Smaller, slower, and worth more per row:
//             ranking is attention, selling is money.
//
// Two things come out. A list of terms the market uses and the seller's title does not, each
// carrying its counts — and, for the Item Specifics the seller left empty, the value those same
// titles agree on, checked against eBay's own published list of legal values for the field.
//
// Everything is OFFERED. Nothing here knows what the item is; it knows what its neighbours are
// called. A seller ticking "Bluetooth" onto a title for an item that has no Bluetooth has bought
// themselves a not-as-described case, so the words arrive as candidates with evidence attached and
// the seller decides. What the miner will not do is offer a word whose only meaning is a promise —
// shipping, speed, hype and eBay's own banned title decoration are struck out before the seller
// ever sees them, because those are the ones that are tempting and false.
//
// Pure and static: no eBay call, no clock, no database. The titles are handed in already fetched.
public static partial class SearchTermMiner
{
    public const int MaxTitleLength = 80;

    // Under this many titles the counts are anecdotes. Six listings agreeing on a word is a market;
    // two is a coincidence, and a coincidence rendered as "60% of results" reads like evidence.
    public const int MinCorpusTitles = 6;

    // A term has to appear in at least this many DISTINCT titles, and this share of them. One
    // seller who stuffs a word into their own listing must not become a recommendation.
    public const int MinTitlesPerTerm = 2;
    public const int MinSharePercent = 20;

    // Two. A third word almost never adds a search anybody types — it adds a longer slice of one
    // competitor's sentence, and it eats three of the eighty characters to say it.
    public const int MaxTermWords = 2;

    // How many terms the built title is allowed to draw from, and how many the answer carries.
    private const int MaxMissingReported = 24;
    private const int MaxSharedReported = 12;

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlnum();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // Grammar. Meaningless alone, and a phrase that starts or ends with one is a fragment
    // ("for the", "with a") rather than a search anybody types.
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "the", "for", "with", "of", "to", "in", "on", "at", "by", "or",
        "from", "is", "are", "be", "as", "it", "its", "this", "that", "these", "those",
        "your", "you", "my", "our", "we", "i", "w", "wo", "plus", "into", "not", "no",
    };

    // Words this will not put in a seller's mouth, whatever the counts say.
    //
    // Everything here is a claim about the transaction rather than a fact about the item, and every
    // one of them is cheap to tick and expensive to get wrong: a title saying "Free Shipping" on a
    // listing that charges for postage is a buyer complaint, and eBay's own title policy bans the
    // decoration words outright. They are also, reliably, the most common words in any corpus of
    // eBay titles — without this list they would top every single answer this ever gives.
    private static readonly HashSet<string> NeverOffer = new(StringComparer.Ordinal)
    {
        // Shipping and handling — promises, not descriptions.
        "free", "ship", "ships", "shipping", "shipped", "shipment", "postage", "delivery",
        "sameday", "overnight", "expedited",
        // Speed and service.
        "fast", "quick", "quickly", "immediate", "instant",
        // Price and hype.
        "sale", "sales", "deal", "deals", "cheap", "bargain", "discount", "clearance",
        "best", "top", "great", "nice", "super", "amazing", "awesome", "excellent",
        "perfect", "beautiful", "gorgeous", "stunning", "premium", "quality",
        // eBay's banned title decoration.
        "lqqk", "l", "look", "wow", "hot", "rare", "htf", "nr", "reserve", "must", "see",
        "bid", "bids", "buy", "now", "sell", "selling", "listing", "listed",
    };

    // ── Reading a title ───────────────────────────────────────────────────────────────────────

    // Words, lowercased, punctuation gone. "Bitmain Antminer S19 Pro 110TH/s" reads as
    // [bitmain, antminer, s19, pro, 110th, s] — the trailing "s" is a stopword-shaped fragment
    // that the term filters below drop, and 110th survives as the number that matters.
    public static List<string> Tokens(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return [];
        return NonAlnum().Split(title.ToLowerInvariant())
            .Where(t => t.Length > 0)
            .ToList();
    }

    private static bool IsPureDigits(string token) => token.All(char.IsDigit);

    // Can this token carry a term on its own?
    private static bool UsableAlone(string token) =>
        token.Length >= 3 &&
        !Stopwords.Contains(token) &&
        !NeverOffer.Contains(token) &&
        !IsPureDigits(token);

    // Can this token sit INSIDE a phrase? Looser — "8" is nothing alone and everything in "8 gb",
    // and "pro" is fine in "s19 pro". Never a banned word, wherever it sits.
    private static bool UsableInPhrase(string token) => !NeverOffer.Contains(token);

    // ── Mining ────────────────────────────────────────────────────────────────────────────────

    private sealed class Candidate
    {
        public required string Key;                 // normalized, space-joined tokens
        public required List<string> Words;
        public readonly HashSet<int> RankedDocs = [];
        public readonly HashSet<int> SoldDocs = [];
        public readonly Dictionary<string, int> Surfaces = new(StringComparer.Ordinal);
    }

    // The surface form to show. Titles are written in every casing there is, so the commonest
    // spelling wins, and ties break toward the one with capitals — "SHA-256" over "sha-256".
    private static string BestSurface(Candidate c) =>
        c.Surfaces.Count == 0
            ? c.Key
            : c.Surfaces
                .OrderByDescending(kv => kv.Value)
                .ThenByDescending(kv => kv.Key.Count(char.IsUpper))
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .First().Key;

    // Pull the run of original words a term came from, so "SHA-256" survives rather than being
    // rebuilt from tokens as "sha 256". Falls back to the tokens when the raw words can't be
    // lined up, which only happens on titles that are pure punctuation.
    private static string SurfaceOf(string rawTitle, int wordIndex, int wordCount)
    {
        var pieces = Whitespace().Split(rawTitle.Trim());
        var kept = new List<string>();
        var i = 0;
        foreach (var piece in pieces)
        {
            var inner = NonAlnum().Split(piece.ToLowerInvariant()).Where(t => t.Length > 0).ToList();
            if (inner.Count == 0) continue;
            // A raw word can hold several tokens ("110TH/s"). It counts as covering all of them.
            if (i + inner.Count > wordIndex && i < wordIndex + wordCount) kept.Add(piece);
            i += inner.Count;
            if (i >= wordIndex + wordCount) break;
        }
        return kept.Count == 0 ? "" : string.Join(' ', kept).Trim(' ', ',', '-', '/', '|', '.');
    }

    private static void Harvest(
        Dictionary<string, Candidate> into, IReadOnlyList<string> titles, int docOffset, bool sold)
    {
        for (var d = 0; d < titles.Count; d++)
        {
            var raw = titles[d] ?? "";
            var tokens = Tokens(raw);
            for (var n = 1; n <= MaxTermWords; n++)
            {
                for (var i = 0; i + n <= tokens.Count; i++)
                {
                    var words = tokens.GetRange(i, n);
                    if (words.Any(w => !UsableInPhrase(w))) continue;
                    if (n == 1)
                    {
                        if (!UsableAlone(words[0])) continue;
                    }
                    else
                    {
                        // A phrase is judged by its ends: "s19 pro" is a term, "s19 for" is a
                        // fragment, and "the antminer" is the same fragment backwards.
                        if (Stopwords.Contains(words[0]) || Stopwords.Contains(words[^1])) continue;
                        // All-numeric phrases ("110 2") are dimensions torn out of context.
                        if (words.All(IsPureDigits)) continue;
                    }

                    var key = string.Join(' ', words);
                    if (!into.TryGetValue(key, out var c))
                        into[key] = c = new Candidate { Key = key, Words = words };

                    if (sold) c.SoldDocs.Add(docOffset + d);
                    else c.RankedDocs.Add(docOffset + d);

                    var surface = SurfaceOf(raw, i, n);
                    if (surface.Length > 0)
                        c.Surfaces[surface] = c.Surfaces.GetValueOrDefault(surface) + 1;
                }
            }
        }
    }

    // One seller, one vote. A listing that has been relisted six times comes back six times under
    // the same title, and every count in this file is "how many DISTINCT listings said this" — a
    // number that stops meaning anything the moment one seller can cast six of the votes. The two
    // corpora are deduplicated separately: a title that both ranks and sold is two facts, not one.
    private static List<string> Distinct(IReadOnlyList<string>? titles)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();
        foreach (var t in titles ?? [])
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            if (seen.Add(Whitespace().Replace(t.Trim(), " "))) kept.Add(t);
        }
        return kept;
    }

    private static bool ContainsRun(List<string> haystack, List<string> needle)
    {
        if (needle.Count > haystack.Count) return false;
        for (var i = 0; i + needle.Count <= haystack.Count; i++)
        {
            var hit = true;
            for (var j = 0; j < needle.Count; j++)
                if (!string.Equals(haystack[i + j], needle[j], StringComparison.Ordinal)) { hit = false; break; }
            if (hit) return true;
        }
        return false;
    }

    /// <summary>
    /// Count what the market calls this thing, and say which of those words the seller's title is
    /// missing. <paramref name="rankedTitles"/> are eBay's top results for the search;
    /// <paramref name="soldTitles"/> are listings that sold. Either may be empty.
    /// </summary>
    public static SearchTermReport Mine(
        string? sellerTitle,
        IReadOnlyList<string> rankedTitles,
        IReadOnlyList<string> soldTitles,
        string query = "")
    {
        var title = Whitespace().Replace((sellerTitle ?? "").Trim(), " ");
        var ranked = Distinct(rankedTitles);
        var sold = Distinct(soldTitles);

        var report = new SearchTermReport
        {
            Query = query,
            YourTitle = title,
            YourTitleLength = title.Length,
            FreeCharacters = Math.Max(0, MaxTitleLength - title.Length),
            RankedTotal = ranked.Count,
            SoldTotal = sold.Count,
        };

        var corpusSize = ranked.Count + sold.Count;
        if (corpusSize == 0)
        {
            report.Status = "no_data";
            report.Headline = "No listings came back for this search";
            report.Message = "Nothing could be read, so nothing is claimed. Try a shorter, plainer set of words in the title — that is what the search is built from.";
            return report;
        }

        if (corpusSize < MinCorpusTitles)
        {
            report.Status = "thin_market";
            report.Headline = $"Only {corpusSize} listing{(corpusSize == 1 ? "" : "s")} to compare against";
            report.Message = $"Fewer than {MinCorpusTitles} is too few to call a pattern, so no words are suggested from it. That is usually good news about the competition — but it means the title is yours to write.";
            return report;
        }

        var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        Harvest(candidates, ranked, 0, sold: false);
        Harvest(candidates, sold, ranked.Count, sold: true);

        // Share is measured against the ranked corpus where there is one, because that is the
        // search the seller is trying to appear in. With no ranked results the sold titles are
        // the only market there is, and become the denominator.
        var denominator = ranked.Count > 0 ? ranked.Count : sold.Count;

        var titleTokens = Tokens(title);

        var scored = candidates.Values
            .Select(c => new
            {
                Cand = c,
                Docs = ranked.Count > 0 ? c.RankedDocs.Count : c.SoldDocs.Count,
                Total = c.RankedDocs.Count + c.SoldDocs.Count,
                Have = ContainsRun(titleTokens, c.Words),
            })
            .Where(x => x.Total >= MinTitlesPerTerm && x.Docs * 100 >= MinSharePercent * denominator)
            // A phrase offered as an addition has to be entirely new. eBay's search results for a
            // given item are near-identical to each other, so every window of a common title scores
            // the same 100% — and half of those windows straddle the seller's own words. "Pro Power"
            // is a real 100% phrase in a corpus of Antminer power supplies, and appending it to a
            // title that already says Pro spends two of the eighty characters saying it twice.
            .Where(x => x.Cand.Words.Count == 1 || x.Have || !x.Cand.Words.Any(w => titleTokens.Contains(w)))
            .OrderByDescending(x => x.Docs * 1000 / Math.Max(1, denominator))
            .ThenByDescending(x => x.Cand.SoldDocs.Count)
            .ThenByDescending(x => x.Cand.Words.Count)
            .ThenByDescending(x => x.Cand.Key.Length)
            .ThenBy(x => x.Cand.Key, StringComparer.Ordinal)
            .ToList();

        // No two terms may share a word. Same reason, from the other side: with a homogeneous
        // corpus "power supply", "supply bitcoin" and "bitcoin miner" all sit at 100%, and reported
        // together they read like three findings when they are one sentence sliced three ways —
        // and applying all three would write "supply" and "bitcoin" into the title twice each.
        //
        // Strongest first, so the word that wins a collision is the one the market agrees on most,
        // and the longer phrase wins a tie because a phrase carries word order and a word does not.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<Candidate>();
        foreach (var x in scored)
        {
            if (x.Cand.Words.Any(seen.Contains)) continue;
            foreach (var w in x.Cand.Words) seen.Add(w);
            kept.Add(x.Cand);
        }

        var terms = kept
            .Select(c => new SearchTerm
            {
                Term = BestSurface(c),
                RankedCount = c.RankedDocs.Count,
                RankedTotal = ranked.Count,
                SoldCount = c.SoldDocs.Count,
                SoldTotal = sold.Count,
                SharePercent = denominator == 0 ? 0
                    : (int)Math.Round(100.0 * (ranked.Count > 0 ? c.RankedDocs.Count : c.SoldDocs.Count) / denominator),
                InYourTitle = ContainsRun(titleTokens, c.Words),
            })
            // A word that sold outranks a word that merely ranks, so the sold count breaks ties
            // before length does.
            .OrderByDescending(t => t.SharePercent)
            .ThenByDescending(t => t.SoldCount)
            .ThenByDescending(t => t.Term.Length)
            .ThenBy(t => t.Term, StringComparer.OrdinalIgnoreCase)
            .ToList();

        report.Missing = terms.Where(t => !t.InYourTitle).Take(MaxMissingReported).ToList();
        report.Shared = terms.Where(t => t.InYourTitle).Take(MaxSharedReported).ToList();

        var (built, added) = BuildTitle(title, report.Missing.Select(t => t.Term));
        report.SuggestedTitle = string.Equals(built, title, StringComparison.Ordinal) ? "" : built;
        report.AddedTerms = added;

        report.Headline = report.Missing.Count == 0
            ? "Your title already carries the words this search runs on"
            : report.Missing.Count == 1
                ? "1 word the listings above you use, and yours doesn't"
                : $"{report.Missing.Count} words the listings above you use, and yours doesn't";

        return report;
    }

    // ── Building the title ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The seller's own title with market terms appended until eBay's 80 characters run out.
    /// Nothing the seller wrote is removed, reordered or reworded — this only ever fills the space
    /// they were not using, so applying it can never lose a word they chose on purpose.
    /// </summary>
    public static (string Title, List<string> Added) BuildTitle(string? sellerTitle, IEnumerable<string> terms)
    {
        var title = Whitespace().Replace((sellerTitle ?? "").Trim(), " ");
        var added = new List<string>();

        // With nothing to build on there is nothing honest to build. A title assembled purely out
        // of other people's keywords describes their items, not this one.
        if (title.Length == 0) return ("", added);

        var have = Tokens(title);
        foreach (var raw in terms)
        {
            var term = Whitespace().Replace((raw ?? "").Trim(), " ");
            if (term.Length == 0) continue;

            var words = Tokens(term);
            if (words.Count == 0 || ContainsRun(have, words)) continue;

            if (title.Length + 1 + term.Length > MaxTitleLength) continue;

            title += " " + term;
            have.AddRange(words);
            added.Add(term);
        }

        return (title, added);
    }

    /// <summary>
    /// The words to actually search eBay with. An 80-character title is not a query — the Browse
    /// API ANDs the words together and a full title matches nothing but itself. A part number is
    /// the tightest handle there is; failing that, the first few words that carry meaning.
    /// </summary>
    public static string BuildQuery(string? title, string? brand = null, string? mpn = null, int maxWords = 6)
    {
        var part = (mpn ?? "").Trim();
        // "Does Not Apply" is eBay's own answer for "there isn't one" and is on thousands of
        // listings. Searching for it returns thousands of unrelated listings.
        if (part.Length >= 3 &&
            !part.Contains("does not apply", StringComparison.OrdinalIgnoreCase) &&
            !part.Contains("n/a", StringComparison.OrdinalIgnoreCase))
        {
            var maker = (brand ?? "").Trim();
            return maker.Length > 0 && !part.Contains(maker, StringComparison.OrdinalIgnoreCase)
                ? maker + " " + part
                : part;
        }

        var words = new List<string>();
        foreach (var piece in Whitespace().Split((title ?? "").Trim()))
        {
            var tokens = Tokens(piece);
            if (tokens.Count == 0) continue;
            if (tokens.All(t => Stopwords.Contains(t) || NeverOffer.Contains(t))) continue;
            words.Add(piece.Trim(' ', ',', '|', '!', '*'));
            if (words.Count >= maxWords) break;
        }

        return words.Count > 0 ? string.Join(' ', words) : (title ?? "").Trim();
    }

    // ── Item Specifics, read off the market and checked against eBay's own list ────────────────

    /// <summary>
    /// For each Item Specific the seller left empty, the value the market's titles agree on —
    /// but only ever a value from eBay's own published list for that aspect, so the worst case is
    /// a legal value that doesn't fit the item rather than a value that fails the publish.
    /// </summary>
    public static List<MarketSpecificSuggestion> SuggestSpecifics(
        IReadOnlyList<CategoryAspect> aspects,
        IReadOnlyDictionary<string, string>? sellerSpecifics,
        IReadOnlyList<string> corpusTitles)
    {
        var titles = Distinct(corpusTitles);
        var results = new List<MarketSpecificSuggestion>();
        if (aspects is null || aspects.Count == 0 || titles.Count < MinCorpusTitles) return results;

        // Which aspects the seller has already answered, under whatever name they used for them.
        var answered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, value) in sellerSpecifics ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var match = AspectMatcher.MatchAspectName(key, aspects);
            if (match is not null) answered.Add(match.Name);
        }

        foreach (var aspect in aspects)
        {
            if (answered.Contains(aspect.Name)) continue;
            // Only aspects eBay published values for. Free-text specifics would mean writing a
            // word lifted off a stranger's listing into a field that describes THIS item.
            if (aspect.Values.Count == 0) continue;
            if (!AspectMatcher.CanInfer(aspect.Name)) continue;

            var votes = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in titles)
            {
                var found = AspectMatcher.FindValueInText(t, aspect.Values, minLength: 3);
                if (found is null) continue;
                votes[found] = votes.GetValueOrDefault(found) + 1;
            }
            if (votes.Count == 0) continue;

            var cast = votes.Values.Sum();
            var winner = votes.OrderByDescending(kv => kv.Value)
                              .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                              .First();

            // Three bars, and a value has to clear all of them. Two listings agreeing is not a
            // market. A value named by a fifth of the corpus is a market. A value the corpus is
            // split down the middle on ("Red" 6, "Blue" 5) is the app not knowing the colour, and
            // the honest answer to not knowing is to leave the field blank for the seller.
            if (winner.Value < MinTitlesPerTerm) continue;
            if (winner.Value * 100 < MinSharePercent * titles.Count) continue;
            if (winner.Value * 10 < cast * 6) continue;

            results.Add(new MarketSpecificSuggestion
            {
                Name = aspect.Name,
                Value = winner.Key,
                Required = aspect.Required,
                Recommended = aspect.Recommended,
                AgreeCount = winner.Value,
                VoteCount = cast,
            });
        }

        // Required first — those are the ones that stop a publish, not just a search.
        return results
            .OrderByDescending(s => s.Required)
            .ThenByDescending(s => s.Recommended)
            .ThenByDescending(s => s.AgreeCount)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
