using System.Text.RegularExpressions;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

// Scores one SoldListings candidate against a target NormalizedProduct using the weighted point
// table from the matching-priority spec (35/25/10/10/10/5/5 = 100), then applies severe penalties
// / hard exclusions for parts-only, broken, empty-box, manual, accessory, compatible/aftermarket,
// replica, wrong generation/capacity/model, and wrong category — so a bad match is dropped instead
// of quietly dragging the price estimate off target. Reuses MarketplaceMatcher for tokenization
// (no re-implementing text normalization) and ProductNormalizer to turn the candidate's own title
// into a comparable NormalizedProduct.
public sealed class ComparableMatcher(ProductNormalizer normalizer)
{
    // Below this, a "match" isn't reliable enough to use even as broad-keyword-tier evidence.
    private const int MinAcceptableConfidence = 20;

    // There is no UPC/EAN/ISBN anywhere in SoldListings — PartNumber (extracted from the title,
    // same as the target) is the closest available "exact identifier" tier for this dataset.
    private const int ExactIdentifierPoints = 35;
    private const int ExactModelPoints = 25;
    private const int BrandPoints = 10;
    private const int CategoryPoints = 10;
    private const int SpecPoints = 10;
    private const int ConditionPoints = 5;
    private const int KeywordPoints = 5;

    public ComparableMatch Match(NormalizedProduct target, MarketplaceComparableResult candidate)
    {
        var candidateProduct = normalizer.Normalize(candidate.Title);
        candidate.Quantity = candidateProduct.Quantity; // feeds MarketPriceEstimator's per-unit normalization
        var penalties = new List<string>();
        double score = 0;

        var partNumberHit = PartNumberMatch(target.PartNumber, candidateProduct.PartNumber);
        if (partNumberHit) score += ExactIdentifierPoints;

        var modelHit = !partNumberHit && ExactMatch(target.Model, candidateProduct.Model);
        if (modelHit) score += ExactModelPoints;

        var brandHit = ExactMatch(target.Brand, candidateProduct.Brand);
        if (brandHit) score += BrandPoints;

        var categoryHit = ExactMatch(target.Category, candidateProduct.Category);
        if (categoryHit) score += CategoryPoints;

        var (specScore, specHit) = ScoreSpecs(target, candidateProduct);
        score += specScore;

        var conditionHit = SameConditionBucket(target.Condition, candidateProduct.Condition);
        if (conditionHit) score += ConditionPoints;

        var keywordCoverage = KeywordCoverage(target.ImportantKeywords, candidateProduct.ImportantKeywords);
        score += keywordCoverage * KeywordPoints;

        // Same ASIC miner is strong model-level evidence, but the normalizer files a miner's
        // hashrate ("90TH") and wattage ("3100W") into the Model field, so ExactMatch on Model
        // under-scores two identical S19 90TH units down below the acceptance floor. Score the
        // miner identity directly: series match earns the model tier, matching rated hashrate the
        // spec points — the positive mirror of the series/hashrate conflict guards below.
        if (LooksLikeMiner(target.RawText) || LooksLikeMiner(candidate.Title))
        {
            var ts = MinerSeries(target.RawText);
            if (ts is not null && ts == MinerSeries(candidate.Title) && !modelHit)
            {
                score += ExactModelPoints;
                modelHit = true;
            }
            var th = MinerHashrate(target.RawText, true);
            var ch = MinerHashrate(candidate.Title, true);
            if (th is not null && ch is not null && th.Value.Unit == ch.Value.Unit
                && Math.Abs(th.Value.Value - ch.Value.Value) <= 0.5 && !specHit)
            {
                score += SpecPoints;
                specHit = true;
            }
        }

        // ── Hard exclusions — a "match" here would actively mislead the price estimate ──────
        string? exclusionReason = null;

        // "For parts", "broken", "untested", "as-is" and "for repair" are ONE condition tier for
        // pricing: every one of them means "this might not run", and that is the only distinction
        // the money cares about. They arrive as different canonical tokens, and comparing tokens
        // instead of tiers rejected the only comps a dead unit can honestly be priced off — an
        // "UNTESTED" target could match neither working comps nor for-parts ones, leaving it with
        // no price at all.
        var targetBroken = BrokenTier(target.NegativeKeywords);
        var candidateBroken = BrokenTier(candidateProduct.NegativeKeywords);

        // Candidate carries a negative-keyword condition the target doesn't (parts/broken/empty
        // box/manual/compatible/replica) — never a valid comparable regardless of title overlap.
        var badCandidateKeywords = candidateProduct.NegativeKeywords
            .Where(k => k is "parts" or "broken" or "empty box" or "manual" or "compatible" or "replica" or "for repair")
            .Where(k => !(targetBroken && k is "parts" or "broken" or "for repair"))
            .Except(target.NegativeKeywords)
            .ToList();
        if (badCandidateKeywords.Count > 0)
            exclusionReason = $"Candidate is {string.Join('/', badCandidateKeywords)}, target is not";

        // ...and the same rule the other way round, which was missing.
        //
        // A dead machine is not worth what a working one is. Only the working-target case was
        // guarded, so an "Antminer S9 | UNTESTED, READ" was priced off comps of tested, running S9s
        // and came back as $500 resale on a $35 buy — a $397 profit that does not exist. Condition
        // was worth 5 points out of 100 here, a nudge, when between "runs" and "might not" it is the
        // whole price.
        //
        // Excluding rather than discounting: what a broken one actually fetches is a real number
        // that real sales know, and this app does not invent numbers. If no for-parts comps exist
        // the row loses its price and says so, which is the honest answer and the one that stops a
        // seller paying working-unit money for scrap.
        if (exclusionReason is null && targetBroken && !candidateBroken)
            exclusionReason = "Target is for parts/untested, candidate is a working unit — a working "
                            + "price is not this item's price";

        // Candidate is a case/cover/accessory listing but the target is the actual product.
        if (exclusionReason is null && !target.IsAccessoryListing &&
            candidateProduct.NegativeKeywords.Any(k => k is "case" or "cover" or "accessory"))
            exclusionReason = "Candidate is an accessory (case/cover), target is the main product";

        // ...and the same rule the other way round, which was missing and is the more expensive
        // direction. An accessory FOR a machine is priced by what the accessory sells for, never by
        // what the machine sells for.
        if (exclusionReason is null && target.IsAccessoryListing && !candidateProduct.IsAccessoryListing &&
            !candidateProduct.NegativeKeywords.Any(k => k is "case" or "cover" or "accessory"))
            exclusionReason = "Target is an accessory, candidate is the main product";

        // A title that names a model to say what it FITS is not a listing for that model. This is
        // the one that produced $981 of imaginary profit on a $38 wrist strap: "1Pc For FANUC
        // A05B-2518-C202 ... Wrist Strap" carries the pendant's part number, so every identity
        // check passed and it was priced off sold teach pendants at 2589% ROI. Excluded in both
        // directions — a real pendant must not be priced off strap sales either.
        if (exclusionReason is null && target.IsCompatibilityListing != candidateProduct.IsCompatibilityListing)
            exclusionReason = target.IsCompatibilityListing
                ? "Target is an accessory sold FOR this model, candidate is the model itself"
                : "Candidate is an accessory sold FOR this model, target is the model itself";

        // Solid precious metal and a plated souvenir of it share every word in the title, and the
        // comp search cannot tell them apart: "1 OZ Gold USA 100 Dollar Bullion Bar" came back at
        // $6.99 off two sold comps on a day gold was $4,604/ozt. Whichever of the two that row
        // really was, it was priced off the other one — and the direction that prices a $7 gold-clad
        // brass bar off real bullion tells the owner to bid two thousand dollars for it. Excluded
        // both ways; see Bullion for why a title that settles nothing is left alone instead.
        if (exclusionReason is null)
        {
            var targetMetal = Bullion.Grade(target.RawText);
            var candidateMetal = Bullion.Grade(candidate.Title);
            if (Bullion.Conflict(targetMetal, candidateMetal))
                exclusionReason = targetMetal is BullionGrade.Solid
                    ? "Candidate is plated/clad/novelty metal, target states solid metal"
                    : "Candidate is solid metal, target is plated/clad/novelty";
        }

        // Both sides have a specific generation/capacity/model/part-number, and they disagree —
        // not "unknown vs known," but two different products (the RTX 4090 vs 4090-Ti case).
        if (exclusionReason is null && ConflictingValue(target.Generation, candidateProduct.Generation))
            exclusionReason = "Generation conflict";
        if (exclusionReason is null && ConflictingValue(target.Capacity, candidateProduct.Capacity))
            exclusionReason = "Capacity conflict";
        // Model conflict only counts when BOTH sides actually look like a model designator (some
        // digit in them) — BuildModel returns whatever text is left over after every other field
        // is extracted, which for a generic listing ("Allen Bradley PLC Controller") is just
        // descriptive prose, not a model number. Two disjoint model designators (e.g. "S19" vs
        // "S21") are a real conflict; "ControlLogix Processor" vs "PLC" isn't — it just means the
        // candidate doesn't state a specific model, which is weaker evidence, not a contradiction.
        // Skipped for ASIC miners: the normalizer leaks the hashrate ("90TH") and wattage
        // ("3100W") into the Model field, so two IDENTICAL S19 90TH units read as conflicting
        // models. Miners are disambiguated below by series + rated hashrate instead, which is
        // exact where the generic Model comparison is noise.
        if (exclusionReason is null && HasDigit(target.Model) && HasDigit(candidateProduct.Model) &&
            !LooksLikeMiner(target.RawText) && !LooksLikeMiner(candidate.Title) &&
            ConflictingValue(target.Model, candidateProduct.Model))
            exclusionReason = "Model number conflict";

        // ── ASIC miner disambiguation ──────────────────────────────────────────────────────
        // A whole class of comps priced an Antminer S19 90TH off a T21 190TH, a Z15, or an L7,
        // because "90T" is a SUBSTRING of "190T", the hashrate isn't one of the tracked spec
        // fields, and the S19/T21/Z15 series token isn't always what the normalizer files under
        // Model. The result priced a used 90T (~$150-250) at ~$556, so the finder told the owner
        // to buy one at $250 for "$92 profit". Two guards, both only firing when a side looks like
        // a miner and both sides actually state the value — same "conflict, not absence" rule as
        // Generation/Capacity above, so a comp that simply omits its hashrate is not excluded.
        if (exclusionReason is null)
            exclusionReason = MinerHashrateConflict(target.RawText, candidate.Title)
                           ?? MinerSeriesConflict(target.RawText, candidate.Title);
        // Strict here too, and for the same reason: under the tolerant comparison two part numbers
        // sharing a prefix were not merely scored as a match, they were not even recognised as a
        // conflict, so nothing downstream got the chance to exclude the comp.
        if (exclusionReason is null
            && !string.IsNullOrWhiteSpace(target.PartNumber)
            && !string.IsNullOrWhiteSpace(candidateProduct.PartNumber)
            && !PartNumberMatch(target.PartNumber, candidateProduct.PartNumber))
            exclusionReason = "Part number conflict";

        // Bundle mismatch — target is a single item, candidate is explicitly a lot/bundle listing
        // of unrelated size, and quantity normalization alone wouldn't make it comparable (a lot
        // of 2+ dissimilar items, not just N of the same unit — approximated here via the "lot"
        // negative keyword combined with a quantity we couldn't actually parse a count for).
        if (exclusionReason is null && candidateProduct.NegativeKeywords.Contains("lot") &&
            candidate.Quantity <= 1 && target.Quantity == 1)
            exclusionReason = "Candidate described as a lot/bundle with no parseable per-unit quantity";

        if (candidate.Quantity != target.Quantity && candidate.Quantity > 0)
            penalties.Add($"Quantity {candidate.Quantity} vs target {target.Quantity} — priced per-unit");

        if (!categoryHit && target.Category is not null && candidateProduct.Category is not null)
        {
            score -= 15;
            penalties.Add("Category mismatch");
        }

        var confidence = (int)Math.Round(Math.Clamp(score, 0, 100));
        var excluded = exclusionReason is not null || confidence < MinAcceptableConfidence;
        if (excluded && exclusionReason is null)
            exclusionReason = $"Match confidence {confidence} below minimum {MinAcceptableConfidence}";

        var tier = partNumberHit ? MatchTier.ExactIdentifier
            : modelHit ? MatchTier.ExactModel
            : brandHit && specHit ? MatchTier.BrandModelSpec
            : keywordCoverage >= 0.5 ? MatchTier.TitleSimilarity
            : MatchTier.BroadKeyword;

        return new ComparableMatch
        {
            Comparable = candidate,
            MatchConfidence = confidence,
            Tier = tier,
            PenaltyReasons = penalties,
            Excluded = excluded,
            ExclusionReason = excluded ? exclusionReason : null,
        };
    }

    // Word-token overlap, not raw equality: a caller's raw model string (e.g. "Antminer S19j
    // Pro", passed straight through by SearchByModelAsync) still needs to match a candidate's own
    // extractor-derived model (e.g. "S19j Pro ASIC" — ProductIdentityExtractor stripped the brand
    // but left a stray category word). Each of the shorter side's tokens is checked as a whole-
    // token-or-prefix match against the longer side (so "S19" also matches a candidate token
    // "S19j") and at least 60% of the shorter side's tokens must hit — tolerant of one side having
    // an extra brand/category word the other doesn't, without conflating genuinely different
    // models (e.g. "S19" vs "S21" shares no token and correctly comes back false).
    private static bool HasDigit(string? s) => !string.IsNullOrEmpty(s) && s.Any(char.IsDigit);

    /// <summary>
    /// "Might not run" — the one condition distinction that moves the price. Every phrase in the
    /// family (for parts, broken, untested, as-is, for repair) normalises into one of these three
    /// canonical tokens, and pricing treats them alike.
    /// </summary>
    private static bool BrokenTier(IEnumerable<string> negativeKeywords) =>
        negativeKeywords.Any(k => k is "parts" or "broken" or "for repair");

    /// <summary>
    /// Part numbers compare whole, not by overlap. Punctuation and case are ignored so
    /// "A06B-6130-H002" and "A06B6130H002" are the same part; anything else is a different part.
    /// </summary>
    /// <remarks>
    /// <see cref="ExactMatch"/> deliberately tolerates 60% token overlap with prefix matching,
    /// which is right for a model ("S19" should match "S19j") and catastrophic for a part number,
    /// where the suffix IS the product. Measured on the live board:
    ///
    ///   target    A06B-6130-K200   a connector plug listed at $36
    ///   candidate A06B-6130-H002   a servo amplifier that sold for $550-$1,256
    ///
    /// Tokenised those are [a06b, 6130, k200] and [a06b, 6130, h002] - two of three segments agree,
    /// the 60% bar is met, and the plug scored the top 35-point exact-identifier tier against thirty
    /// amplifier comps. The board then reported it as the day's best deal: $421 net, 1187% ROI,
    /// flagged Goldmine. Every number downstream was arithmetically correct and the row was fiction.
    ///
    /// A wrong comp is worse than no comp: no comp shows as "no sold data" and gets checked by hand,
    /// while a wrong one gets bought.
    /// </remarks>
    public static bool PartNumberMatch(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;

        static string Key(string s)
        {
            Span<char> buf = stackalloc char[s.Length];
            var n = 0;
            foreach (var c in s)
                if (char.IsLetterOrDigit(c)) buf[n++] = char.ToUpperInvariant(c);
            return new string(buf[..n]);
        }

        var ka = Key(a);
        var kb = Key(b);
        return ka.Length > 0 && ka == kb;
    }

    private static bool ExactMatch(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        var wordsA = MarketplaceMatcher.Words(MarketplaceMatcher.Normalize(a));
        var wordsB = MarketplaceMatcher.Words(MarketplaceMatcher.Normalize(b));
        if (wordsA.Count == 0 || wordsB.Count == 0) return false;

        var (shorter, longer) = wordsA.Count <= wordsB.Count ? (wordsA, wordsB) : (wordsB, wordsA);
        var matched = shorter.Count(t => longer.Any(l =>
            l.StartsWith(t, StringComparison.Ordinal) || t.StartsWith(l, StringComparison.Ordinal)));
        return matched >= Math.Max(1, (int)Math.Ceiling(shorter.Count * 0.6));
    }

    // True only when both sides have a specific value AND neither contains the other — missing-
    // on-one-side, or one being a superset of the other (see ExactMatch), is not a conflict.
    private static bool ConflictingValue(string? target, string? candidate) =>
        !string.IsNullOrWhiteSpace(target) && !string.IsNullOrWhiteSpace(candidate) && !ExactMatch(target, candidate);

    private static (double Score, bool Hit) ScoreSpecs(NormalizedProduct target, NormalizedProduct candidate)
    {
        (string? T, string? C)[] pairs =
        [
            (target.Capacity, candidate.Capacity), (target.Size, candidate.Size),
            (target.Generation, candidate.Generation), (target.Voltage, candidate.Voltage),
            (target.Revision, candidate.Revision), (target.Processor, candidate.Processor),
            (target.Ram, candidate.Ram), (target.Storage, candidate.Storage),
            (target.Color, candidate.Color),
        ];

        var applicable = pairs.Count(p => !string.IsNullOrWhiteSpace(p.T));
        if (applicable == 0) return (0, false);

        var matched = pairs.Count(p => ExactMatch(p.T, p.C));
        return (SpecPoints * ((double)matched / applicable), matched > 0);
    }

    // Coarse condition buckets — real listing text is too varied ("Pre-Owned" vs "Used" vs "Good")
    // for an exact-string match to ever fire, so bucket first the way a buyer actually thinks
    // about condition, and reward agreement within a bucket.
    private static readonly (string Bucket, string[] Members)[] ConditionBuckets =
    [
        ("new", ["brand new", "new open box", "sealed", "new"]),
        ("likenew", ["like new", "open box", "excellent"]),
        ("used", ["pre-owned", "used", "good", "fair", "refurbished", "working", "tested working"]),
        ("broken", ["for parts", "not working", "broken", "damaged", "untested", "poor"]),
    ];

    private static bool SameConditionBucket(string? target, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(candidate)) return false;
        var t = BucketOf(target);
        var c = BucketOf(candidate);
        return t is not null && t == c;
    }

    private static string? BucketOf(string condition) =>
        ConditionBuckets.FirstOrDefault(b => b.Members.Any(m =>
            condition.Contains(m, StringComparison.OrdinalIgnoreCase))).Bucket;

    private static double KeywordCoverage(List<string> targetKeywords, List<string> candidateKeywords)
    {
        if (targetKeywords.Count == 0) return 0;
        var candidateSet = new HashSet<string>(candidateKeywords, StringComparer.OrdinalIgnoreCase);
        var matched = targetKeywords.Count(candidateSet.Contains);
        return (double)matched / targetKeywords.Count;
    }

    // ── ASIC miner disambiguation ──────────────────────────────────────────────────────────
    private static readonly string[] MinerContext =
        ["antminer", "whatsminer", "bitmain", "avalon", "goldshell", "iceriver", "jasminer",
         "ipollo", "sealminer", "asic miner", "th/s", "gh/s", "mh/s", "ph/s"];

    private static bool LooksLikeMiner(string? text) =>
        !string.IsNullOrEmpty(text) && MinerContext.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    // The rated hashrate as (number, unit), e.g. "90TH/s" -> (90,'T'), "9.05 Gh" -> (9.05,'G').
    // Explicit unit ("TH","Gh","MH/s") first; a bare "<n>T" is only read as a hashrate in miner
    // context, so "4TB" and a "90 min" spec elsewhere are never mistaken for one. First hashrate
    // in the title wins — it is the machine's rated figure ("S19 90TH ... 100TH on board").
    private static readonly Regex HashrateExplicit =
        new(@"(\d+(?:\.\d+)?)\s*([TGMP])[Hh](?:/?s)?\b", RegexOptions.Compiled);
    private static readonly Regex HashrateBareT =
        new(@"(\d+(?:\.\d+)?)\s*[Tt]\b", RegexOptions.Compiled);

    private static (double Value, char Unit)? MinerHashrate(string? text, bool isMiner)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = HashrateExplicit.Match(text);
        if (m.Success && double.TryParse(m.Groups[1].Value, out var v))
            return (v, char.ToUpperInvariant(m.Groups[2].Value[0]));
        if (isMiner)
        {
            var b = HashrateBareT.Match(text);
            if (b.Success && double.TryParse(b.Groups[1].Value, out var bv)) return (bv, 'T');
        }
        return null;
    }

    private static string? MinerHashrateConflict(string? targetText, string? candidateText)
    {
        var tMiner = LooksLikeMiner(targetText);
        var cMiner = LooksLikeMiner(candidateText);
        if (!tMiner && !cMiner) return null;
        var a = MinerHashrate(targetText, tMiner || cMiner);
        var b = MinerHashrate(candidateText, tMiner || cMiner);
        if (a is null || b is null) return null;
        if (a.Value.Unit != b.Value.Unit || Math.Abs(a.Value.Value - b.Value.Value) > 0.5)
            return $"Hashrate conflict ({a.Value.Value:0.##}{a.Value.Unit}H vs {b.Value.Value:0.##}{b.Value.Unit}H)";
        return null;
    }

    // The model/series token anchored on a known ASIC prefix (S19, T21, Z15, L7, E9, A1246, M50S,
    // KS3, KA3, KD6). Anchoring on the prefix keeps "SHA256", "3260W" and "PSU" from ever reading
    // as a model, and first-match returns the model that sits right after the brand.
    private static readonly Regex MinerSeriesToken =
        new(@"\b((?:KS|KA|KD|DZ|M|S|T|L|Z|E|X|A)\d{1,4}[A-Za-z]{0,3}\d{0,2})\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string? MinerSeries(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = MinerSeriesToken.Match(text);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static string? MinerSeriesConflict(string? targetText, string? candidateText)
    {
        if (!LooksLikeMiner(targetText) && !LooksLikeMiner(candidateText)) return null;
        var a = MinerSeries(targetText);
        var b = MinerSeries(candidateText);
        if (a is null || b is null) return null;
        // Prefix-tolerant, like ExactMatch on a model: a bare "S19" query must still find "S19j"
        // (broad discovery, and their hashrates are what separate them below), while "S19" vs
        // "T21"/"Z15"/"S21" — neither a prefix of the other — is a genuinely different machine.
        if (a == b || a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal))
            return null;
        return $"Miner series conflict ({a} vs {b})";
    }
}
