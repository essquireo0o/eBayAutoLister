using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The WhatsNot card's one blind spot, answered: whether the lot on screen is the thing the card is
/// pricing. Given what the item box says and what a look at the lot's photograph found, this decides
/// whether the picture <b>agrees</b> with the name, <b>sharpens</b> it, <b>contradicts</b> it, or
/// can't say — and, only when there is something better, offers a name to price on instead.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this matters more here than anywhere else in the app.</b> Every figure on the live card is
/// derived from one eBay sold search, and that search runs on a name. At a desk a wrong name costs a
/// re-type. On a live show it costs a bid, because the ceiling arrives looking exactly as confident
/// as a right one. Live lots are named by somebody holding a camera while talking: "MYSTERY MINER
/// LOT", "S19 read desc", "Antminer!!!". The photograph is the only evidence on the screen that can
/// disagree with the name, and the show read already brings it back.
/// </para>
/// <para>
/// <b>What it is not allowed to do.</b> It prices nothing — <see cref="LotPhotoLook"/> carries no
/// dollar figure and a test holds it that way. It changes nothing — a suggested name is an offer the
/// seller presses, never a substitution. And it does not adjust money for the condition it sees: a
/// ceiling made of sales that happened is stronger evidence than a compressed frame, and a screen
/// that quietly shaded the ceiling on a glimpse of a scuff would be trading the better evidence for
/// the worse one.
/// </para>
/// <para>
/// <b>The bar for crying wolf.</b> A check that is wrong about a disagreement is worse than no check:
/// the seller loses the lot AND stops trusting the panel. So <see cref="LotPhotoAgreement.Differs"/>
/// is claimed only when both names carry a model-shaped token and none of them match, or when two
/// specific names share nothing at all. A photograph that read no model number cannot contradict a
/// name that has one — that is <see cref="LotPhotoAgreement.Unsure"/>, and it says so.
/// </para>
/// <para>
/// Pure. Every sentence the panel shows is written here rather than in the browser, so the app has
/// one account of what a picture said.
/// </para>
/// </remarks>
public static class LotPhotoJudge
{
    /// <summary>Confidences the look is allowed to hand back. Anything else reads as
    /// <see cref="Low"/>, because the cautious reading of an unknown confidence claim is the only
    /// safe one — see <see cref="ClaudeService"/>, which normalises it the same way.</summary>
    public const string High = "high";
    public const string Medium = "medium";
    public const string Low = "low";

    /// <summary>
    /// Words that decorate a lot name and never identify a thing. Removed before two names are
    /// compared, so "Antminer S19j Pro (used, free ship)" and "Bitmain Antminer S19j Pro" are not
    /// held to disagree over the packaging.
    /// </summary>
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "plus", "from", "your", "you", "its",
        "new", "used", "oem", "genuine", "authentic", "original",
        "free", "ship", "shipping", "shipped", "fast", "usa", "us",
        "read", "desc", "description", "details", "please", "see", "look", "note",
        "lot", "item", "items", "pcs", "pieces", "piece", "set", "pack", "unit", "units",
        "sale", "auction", "bid", "bids", "win", "wow", "nice", "great", "good", "clean",
    };

    /// <summary>
    /// Names that are deliberately not a name. A title built out of these cannot be contradicted by
    /// anything — there is no claim in it to be wrong — so a photograph that names the lot has
    /// sharpened it rather than disagreed with it.
    /// </summary>
    private static readonly HashSet<string> Vague = new(StringComparer.OrdinalIgnoreCase)
    {
        "mystery", "random", "surprise", "assorted", "mixed", "misc", "miscellaneous",
        "unknown", "various", "grab", "bag", "bundle", "box", "bin", "junk", "stuff",
        "thing", "things", "whatever", "untitled", "tbd",
    };

    /// <summary>
    /// One look at one photograph, judged against the name the card is being priced on.
    /// </summary>
    /// <param name="typedTitle">What the item box said. Empty is an answer, not an error.</param>
    /// <param name="identity">What the photo was named as. Already normalised by
    /// <see cref="ClaudeService.IdentifyItemAsync"/>.</param>
    public static LotPhotoLook Judge(string? typedTitle, SnapIdentity? identity, string imageUrl)
    {
        var look = new LotPhotoLook
        {
            ImageUrl = imageUrl ?? "",
            TypedTitle = LiveLotList.Clean(typedTitle ?? "").Title,
        };

        var rawSeen = (identity?.Title ?? "").Trim();
        // The same clean a pasted lot line goes through, so a name off a photograph and the same name
        // typed by hand reach the comp lookup identically. Two cleanings would be two names.
        var seen = LiveLotList.Clean(rawSeen).Title;

        if (!Searchable(seen))
        {
            look.Status = LotPhotoStatuses.Unnamed;
            look.Headline = "Nothing in that photo named a product.";
            look.Detail = rawSeen.Length > 0
                ? $"The closest it got was \"{rawSeen}\", which is not something a sold search can run on."
                : "A live show's photo is often a box on a table at 480p, and that is a real thing to " +
                  "be handed rather than a failure of anything.";
            look.Hint = "Type what it is and press ⚡ Price it — that is still the whole feature.";
            return look;
        }

        look.Status = LotPhotoStatuses.Looked;
        look.SeenTitle = seen;
        look.Brand = (identity!.Brand ?? "").Trim();
        look.Model = (identity.Model ?? "").Trim();
        look.Certainty = Normalize(identity.Certainty);
        look.Condition = (identity.Condition ?? "").Trim();
        look.ConditionNote = (identity.ConditionNote ?? "").Trim();

        var check = (identity.CheckThis ?? "").Trim();
        if (check.Length > 0)
        {
            // Reframed on purpose. At a yard sale the seller picks the thing up; on a live show they
            // cannot — but the chat is open and the host is holding it, which makes this the most
            // actionable line on the panel and the only one that is a question.
            look.AskTheHost = $"Ask the host while it's still on the block: {LowerFirst(check)}";
        }

        var (agreement, headline, detail) = Compare(look.TypedTitle, seen, look.Certainty);
        look.Agreement = agreement;
        look.Headline = headline;
        look.Detail = detail;
        look.SuggestedTitle = Suggestion(agreement, look.TypedTitle, seen, look.Certainty);
        look.Warnings.AddRange(Warnings(look));
        look.Evidence.AddRange(Evidence(look, rawSeen));

        return look;
    }

    /// <summary>
    /// Whether the picture and the name are about the same product, and what that means for the card
    /// below. Public because "what did the app decide the photo said" is the thing worth testing.
    /// </summary>
    public static (string Agreement, string Headline, string Detail) Compare(
        string typed, string seen, string certainty)
    {
        // Nothing to disagree with. The photo's name is the only one there is, which makes it worth
        // reading rather than trusting.
        if (!Searchable(typed))
        {
            return (LotPhotoAgreement.OnlyName,
                $"The photo shows {seen}.",
                "Nothing is in the item box, so this is the only name on the screen. Read it before " +
                "you price on it — everything the card says will come from a sold search on it.");
        }

        // A guess is not evidence. Below this bar the look is allowed to say what it thinks it saw
        // and nothing else: it cannot confirm the name, cannot contradict it, and offers nothing.
        if (certainty == Low)
        {
            return (LotPhotoAgreement.Unsure,
                "The photo isn't clear enough to name the lot.",
                $"It reads as {seen}, with low confidence — too low to price against or to argue with " +
                $"\"{typed}\". Nothing here changes the card below.");
        }

        var typedTokens = Significant(typed);
        var seenTokens = Significant(seen);
        var typedModels = typedTokens.Where(ModelShaped).ToList();
        var seenModels = seenTokens.Where(ModelShaped).ToList();
        var shared = typedTokens.Intersect(seenTokens, StringComparer.OrdinalIgnoreCase).ToList();

        // "Mystery miner lot" makes no claim, so nothing can contradict it. Everything the photo
        // found is new information, which is the case this whole feature exists for.
        if (IsVague(typedTokens, typedModels))
        {
            return (LotPhotoAgreement.Sharpens,
                $"The photo says {seen}.",
                $"\"{typed}\" doesn't name a product, so a sold search on it answers at random. This is " +
                "a name that search can actually run on.");
        }

        if (typedModels.Count > 0 && seenModels.Count > 0)
        {
            if (typedModels.Intersect(seenModels, StringComparer.OrdinalIgnoreCase).Any())
            {
                return seenModels.Except(typedModels, StringComparer.OrdinalIgnoreCase).Any()
                    ? (LotPhotoAgreement.Sharpens,
                        $"The photo says {seen}.",
                        "Same product, and the photo carries a spec the box doesn't. That spec is often " +
                        "what separates two very different sold prices.")
                    : (LotPhotoAgreement.Agrees,
                        $"The photo matches what you're pricing: {seen}.",
                        "The model in the box and the model in the picture are the same one. Nothing to " +
                        "change — the ceiling below is priced on the right thing.");
            }

            return (LotPhotoAgreement.Differs,
                $"The photo doesn't look like \"{typed}\" — it looks like {seen}.",
                "The card below is priced on a sold search for what's in the box. If the photo is right, " +
                "that ceiling belongs to a different product and the room it shows is not real.");
        }

        // Two specific names with not one word in common. Whatever the picture is of, it is not the
        // thing the card below is pricing.
        if (shared.Count == 0)
        {
            return (LotPhotoAgreement.Differs,
                $"The photo doesn't look like \"{typed}\" — it looks like {seen}.",
                "The two names have nothing in common. The card below is priced on what's in the box, " +
                "so if the photo is right, its ceiling is about something else.");
        }

        // Same kind of thing, and the plate wasn't legible. A picture that read no model number
        // cannot tell a 12 from a 13, and saying otherwise would be the panel crying wolf — which
        // costs the seller the lot AND the panel.
        if (typedModels.Count > 0 && seenModels.Count == 0)
        {
            return (LotPhotoAgreement.Unsure,
                $"The photo shows {seen}, with no model number legible on it.",
                $"That neither confirms nor contradicts \"{typed}\" — the picture agrees about what KIND " +
                "of thing it is and can't tell two models of it apart. The card below is unchanged.");
        }

        // The photograph read a model number and the box has none. That is the plate on the side of
        // the machine, and it is the single most valuable thing a picture can hand a comp search.
        if (seenModels.Count > 0)
        {
            return (LotPhotoAgreement.Sharpens,
                $"The photo says {seen}.",
                $"\"{typed}\" carries no model number and the photo does. Sold prices split hard on the " +
                "model, so this is the difference between a price and a range.");
        }

        return (LotPhotoAgreement.Agrees,
            $"The photo matches what you're pricing: {seen}.",
            "Nothing to change — what's in the box and what's in the picture are the same thing.");
    }

    /// <summary>
    /// The name to offer instead, or nothing. Four separate reasons to offer nothing, and every one
    /// of them is the same reason: a name the seller did not choose must be better than the one they
    /// did, or it has no business being on the screen.
    /// </summary>
    public static string Suggestion(string agreement, string typed, string seen, string certainty)
    {
        // A confidence too low to argue with is a confidence too low to act on.
        if (certainty == Low) return "";

        // Agreeing and being unsure are both "carry on" — an offer under either would be the panel
        // asking the seller to make a decision it has just told them they don't have to make.
        if (agreement is not (LotPhotoAgreement.Sharpens or LotPhotoAgreement.Differs or LotPhotoAgreement.OnlyName))
            return "";

        // The same bar the pasted lot list holds a line to. A name the comp lookup can't run on is
        // not an improvement on one it can.
        if (!Searchable(seen)) return "";

        return string.Equals(seen, typed, StringComparison.OrdinalIgnoreCase) ? "" : seen;
    }

    /// <summary>
    /// What the photograph cannot tell the seller. Never empty on a successful look: a picture names
    /// a product, and a bid is a claim about a working one.
    /// </summary>
    public static List<string> Warnings(LotPhotoLook look)
    {
        var warnings = new List<string>();

        if (look.Status != LotPhotoStatuses.Looked) return warnings;

        if (look.Agreement == LotPhotoAgreement.Differs)
        {
            warnings.Add($"Every number on the card below came from a sold search on \"{look.TypedTitle}\". " +
                         "Nothing has changed it — the ceiling, the room and the profit are still that " +
                         "search's answer, about that name.");
        }

        if (look.Certainty == Medium)
        {
            warnings.Add("Named with medium confidence — the shape is right and the plate may not have " +
                         "been legible. Worth a glance at the picture before the hammer.");
        }

        if (look.Condition is "FOR_PARTS_OR_NOT_WORKING" or "USED_ACCEPTABLE")
        {
            warnings.Add("The photo looks like a rough one, and the sold comps behind the ceiling are " +
                         "whatever the search returned — mostly working units. Nothing on this panel " +
                         "has taken money off the ceiling for it; that is still your call.");
        }

        warnings.Add("A photo can name a product. It cannot tell you it powers on, that nothing is " +
                     "missing from the box, or that the host's description is true.");

        return warnings;
    }

    private static List<string> Evidence(LotPhotoLook look, string rawSeen)
    {
        var evidence = new List<string>();

        if (rawSeen.Length > 0 && !string.Equals(rawSeen, look.SeenTitle, StringComparison.Ordinal))
            evidence.Add($"Read off the photo as \"{rawSeen}\", shortened to \"{look.SeenTitle}\" for the search.");

        if (look.Brand.Length > 0) evidence.Add($"Brand on the photo: {look.Brand}");
        if (look.Model.Length > 0) evidence.Add($"Model on the photo: {look.Model}");

        evidence.Add($"Confidence it named the right product: {look.Certainty}");

        if (look.Condition.Length > 0)
        {
            evidence.Add($"Condition from visible wear only: {look.Condition}" +
                         (look.ConditionNote.Length > 0 ? $" — {look.ConditionNote}" : ""));
        }

        evidence.Add(look.TypedTitle.Length > 0
            ? $"Compared against what the card is pricing: \"{look.TypedTitle}\""
            : "Nothing was in the item box when the photo was looked at.");

        return evidence;
    }

    // ── The refusals ─────────────────────────────────────────────────────────────────────────

    /// <summary>No read has brought a photo back yet. An empty state, not a failure.</summary>
    public static LotPhotoLook NoPhoto() => new()
    {
        Status = LotPhotoStatuses.NoPhoto,
        Headline = "There's no lot photo to look at yet.",
        Detail = "The photo comes back with 📡 Read the show — it is the lot's own picture off the " +
                 "show's page, not a frame grabbed from the video.",
        Hint = "Read the show first, or type the item in and press ⚡ Price it — that never needed a photo.",
    };

    /// <summary>Anything that stopped the look before it started. Never without a next move.</summary>
    public static LotPhotoLook Refuse(string status, string headline, string detail, string hint) => new()
    {
        Status = status,
        Headline = headline,
        Detail = detail,
        Hint = hint,
    };

    /// <summary>The photo arrived and the look at it did not.</summary>
    public static LotPhotoLook Failed(FailureInfo failure) => new()
    {
        Status = LotPhotoStatuses.Failed,
        Headline = failure.Headline,
        Detail = failure.WhatHappened,
        Hint = failure.WhatToDo.Length > 0
            ? failure.WhatToDo
            : "Type the item in and press ⚡ Price it — the ceiling never needed the photo.",
    };

    // ── Reading a name ───────────────────────────────────────────────────────────────────────

    /// <summary>The same bar the pasted lot list holds a line to, shared rather than re-picked.</summary>
    public static bool Searchable(string? title) =>
        title is { Length: >= LiveLotList.MinTitleLength } && title.Any(char.IsLetter);

    /// <summary>
    /// The words in a name that carry identity. Case-folded, punctuation dropped, decoration removed.
    /// </summary>
    public static List<string> Significant(string? title)
    {
        var words = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in Tokens(title))
        {
            if (Noise.Contains(token)) continue;
            if (seen.Add(token)) words.Add(token);
        }

        return words;
    }

    private static IEnumerable<string> Tokens(string? title)
    {
        var buffer = new System.Text.StringBuilder();

        foreach (var ch in (title ?? "") + " ")
        {
            if (char.IsLetterOrDigit(ch)) { buffer.Append(char.ToLowerInvariant(ch)); continue; }
            if (buffer.Length >= 2) yield return buffer.ToString();
            buffer.Clear();
        }
    }

    /// <summary>
    /// A token that identifies a variant rather than a kind of thing: "s19j", "104th", "dcd771",
    /// "12". Letters alone are a category; letters with a number in them are usually the difference
    /// between two prices.
    /// </summary>
    public static bool ModelShaped(string token) =>
        token.Length >= 2 && token.Any(char.IsDigit);

    /// <summary>
    /// A name that makes no claim — a mystery box, a one-word category. Nothing can contradict it,
    /// so a photograph that names the lot has sharpened it rather than disagreed with it.
    /// </summary>
    public static bool IsVague(List<string> tokens, List<string> models) =>
        models.Count == 0 && (tokens.Count <= 1 || tokens.Any(Vague.Contains));

    private static string Normalize(string? certainty) => (certainty ?? "").Trim().ToLowerInvariant() switch
    {
        High => High,
        Medium => Medium,
        _ => Low,
    };

    private static string LowerFirst(string sentence) =>
        sentence.Length == 0 ? sentence : char.ToLowerInvariant(sentence[0]) + sentence[1..];
}
