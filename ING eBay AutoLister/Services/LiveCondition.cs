using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// What condition the lot on screen is in, what condition the sold comps behind its ceiling were
/// in, and what the gap between those two is allowed to do to the money.
/// </summary>
/// <remarks>
/// <para>
/// The live card has always priced an item against a median of everything that matched its name.
/// On eBay, "everything that matched" routinely means <b>sealed boxes and beaten-up used units in
/// the same list</b>, and the median across them is the right price for neither. The seller is the
/// one person who can tell the difference — they are looking at the thing through a camera while
/// the auctioneer talks — and until now there was nothing on this screen for them to say it with.
/// </para>
/// <para>
/// <b>Why this cannot be done in the search.</b> The comp lookup is a boolean AND over sold
/// <i>titles</i> (see <see cref="LiveSearchQuery"/>), and eBay's condition is a <i>field</i>, not a
/// word sellers put in their titles. Adding "used" to the query would return almost nothing and the
/// card would say CAN'T PRICE IT about an item with a perfectly good used market. So this reads the
/// condition column on comps <b>already in hand</b> — no second lookup, no network, no clock — which
/// is also what makes changing the condition box re-answer instantly off held comps.
/// </para>
///
/// <para><b>The asymmetry, which is the same one the trend read is built around.</b> A lot in a
/// worse condition than its comps <b>cuts</b> the ceiling. A lot in a better condition than its
/// comps does <b>not</b> raise it. Refusing to bid up on a sealed box costs a lot somebody else
/// wins — invisible, and there is another one in four minutes. Failing to cut the ceiling on a
/// used one costs real cash on a purchase with no undo, and the loss only shows up weeks later when
/// the thing sells for what used ones actually fetch. "It's sealed" is also a claim about an object
/// being held up to a camera by the person selling it; "it's used" is the seller's own eyes.</para>
///
/// <para><b>What it refuses to do.</b></para>
/// <list type="bullet">
/// <item><description><b>Guess the lot's condition.</b> Silence in a lot's name is not evidence of
/// anything. An unstated lot is priced exactly as it was before this file existed and the card
/// <i>asks</i>, showing the band mix so the seller can see what the question is worth answering.
/// One press then re-prices off comps already held.</description></item>
/// <item><description><b>Invent a haircut.</b> The cut is the ratio between what the matching band
/// actually sold for and what all the classified comps sold for — measured, off the same rows, with
/// the same median function the price estimator uses. There is no "used items are worth 60%"
/// constant anywhere in here.</description></item>
/// <item><description><b>Price off two comps.</b> Below <see cref="MinBandComps"/> sold rows in the
/// lot's own band, nothing is cut and the card says out loud that the ceiling above it is a
/// wrong-condition ceiling. Saying so is the whole value in that case — it is the one state where
/// the badge is knowingly optimistic.</description></item>
/// </list>
/// <para>Pure and deterministic: no clock, no network, no state. A card re-priced from held comps
/// re-runs exactly this reading and gets exactly this answer.</para>
/// </remarks>
public static class LiveCondition
{
    /// <summary>
    /// The fewest sold comps in the lot's own condition band before that band's median is allowed
    /// to move the ceiling. The same bar the auction sniper refuses to bid under — a band median
    /// off two sales is arithmetic, not evidence, and this one takes money off.
    /// </summary>
    public const int MinBandComps = AuctionSniperAnalyzer.MinCompsToBid;

    /// <summary>
    /// How many of the comps have to state a condition before the bands are read at all. Under
    /// this, "the comps are 80% new" would be a claim about three rows out of twenty.
    /// </summary>
    public const decimal MinCoveragePercent = 50m;

    /// <summary>The fewest classified rows, regardless of what share of the set they are.</summary>
    public const int MinClassifiedComps = 4;

    /// <summary>
    /// The most the ceiling is ever cut for condition, however far the bands are apart. Past this
    /// the gap has stopped looking like the same product in worse shape and started looking like a
    /// different product — a "for parts" row that is one screw, sitting in a list of working units.
    /// </summary>
    public const decimal MaxHaircutPercent = 50m;

    // ── The ladder ───────────────────────────────────────────────────────────────────────────

    /// <summary>Worst to best. The order is the whole point: it is what lets "the comps are in a
    /// better condition than this lot" be a question with an answer.</summary>
    public static int Rank(string? band) => band switch
    {
        LiveConditionBands.Broken => 0,
        LiveConditionBands.Used => 1,
        LiveConditionBands.LikeNew => 2,
        LiveConditionBands.New => 3,
        _ => -1,
    };

    public static string Label(string? band) => band switch
    {
        LiveConditionBands.Broken => "For parts / not working",
        LiveConditionBands.Used => "Used",
        LiveConditionBands.LikeNew => "Open box / like new",
        LiveConditionBands.New => "New / sealed",
        _ => "Condition not stated",
    };

    /// <summary>The short word the strip and the picker use.</summary>
    public static string ShortLabel(string? band) => band switch
    {
        LiveConditionBands.Broken => "for parts",
        LiveConditionBands.Used => "used",
        LiveConditionBands.LikeNew => "open box",
        LiveConditionBands.New => "new",
        _ => "unstated",
    };

    /// <summary>
    /// The condition vocabulary, checked in this order — most specific first, so "like new" is
    /// never read as "new" and "new open box" is never read as sealed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two sources feed this and they are not the same kind of text, which is what
    /// <c>InATitle</c> is for. A comp's condition is a <b>field</b>, near enough a controlled
    /// vocabulary — "Pre-Owned", "For parts or not working", "Seller refurbished". A lot's name is
    /// <b>prose</b> shouted by an auctioneer, where "good", "excellent" and "fair" are as likely to
    /// be describing the deal as the item. So the graded adjectives are read off the field and
    /// never off the name.
    /// </para>
    /// <para>
    /// Refurbished sits in <c>used</c>, which is where <see cref="ComparableMatcher"/> has always
    /// put it. It is arguably its own tier on eBay; what matters far more is that the app has one
    /// answer rather than two.
    /// </para>
    /// </remarks>
    private static readonly (string Phrase, string Band, bool InATitle)[] Vocabulary =
    [
        // Dead or damaged. First, because "for parts not working" contains "working".
        ("for parts", LiveConditionBands.Broken, true),
        ("parts only", LiveConditionBands.Broken, true),
        ("parts or repair", LiveConditionBands.Broken, true),
        ("not working", LiveConditionBands.Broken, true),
        ("non working", LiveConditionBands.Broken, true),
        ("untested", LiveConditionBands.Broken, true),
        // "as-is" flattens to the same three characters, so one entry covers both spellings.
        ("as is", LiveConditionBands.Broken, true),
        ("salvage", LiveConditionBands.Broken, true),
        ("spares", LiveConditionBands.Broken, true),
        ("broken", LiveConditionBands.Broken, true),
        ("damaged", LiveConditionBands.Broken, true),
        ("cracked", LiveConditionBands.Broken, true),
        ("poor", LiveConditionBands.Broken, false),

        // Opened but effectively unused. Before the new band, so "like new" and "new (other)" —
        // eBay's own wording for an opened new item — never read as sealed.
        ("like new", LiveConditionBands.LikeNew, true),
        ("open box", LiveConditionBands.LikeNew, true),
        ("openbox", LiveConditionBands.LikeNew, true),
        ("new other", LiveConditionBands.LikeNew, true),
        ("new with defects", LiveConditionBands.LikeNew, true),
        ("mint", LiveConditionBands.LikeNew, true),

        // Sealed.
        ("brand new", LiveConditionBands.New, true),
        ("factory sealed", LiveConditionBands.New, true),
        ("sealed", LiveConditionBands.New, true),
        ("unopened", LiveConditionBands.New, true),
        ("new in box", LiveConditionBands.New, true),
        ("nib", LiveConditionBands.New, true),
        ("bnib", LiveConditionBands.New, true),
        ("nwt", LiveConditionBands.New, true),
        ("new with tags", LiveConditionBands.New, true),
        ("nos", LiveConditionBands.New, true),
        ("new", LiveConditionBands.New, true),

        // Everything that has been owned. Last: "used" is a substring of nothing above it, and the
        // graded adjectives here only ever come off a condition field.
        ("pre owned", LiveConditionBands.Used, true),
        ("preowned", LiveConditionBands.Used, true),
        ("refurbished", LiveConditionBands.Used, true),
        ("refurb", LiveConditionBands.Used, true),
        ("renewed", LiveConditionBands.Used, true),
        ("second hand", LiveConditionBands.Used, true),
        ("used", LiveConditionBands.Used, true),
        ("tested working", LiveConditionBands.Used, true),
        ("tested", LiveConditionBands.Used, true),
        ("working", LiveConditionBands.Used, true),
        ("excellent", LiveConditionBands.Used, false),
        ("very good", LiveConditionBands.Used, false),
        ("good", LiveConditionBands.Used, false),
        ("acceptable", LiveConditionBands.Used, false),
        ("fair", LiveConditionBands.Used, false),
    ];

    /// <summary>
    /// Lowercased, punctuation flattened to single spaces and padded with one at each end — so a
    /// phrase lookup is a plain substring test with word boundaries built in.
    /// </summary>
    /// <remarks>
    /// Three things fall out of this and all three are load-bearing. "Unused" does not contain
    /// " used ". "Pre-Owned" and "Pre Owned" are the same string by the time they get here. And
    /// runs of punctuation collapse, so eBay's own <c>New (other)</c> — which would otherwise carry
    /// two spaces in the middle — is matched by the phrase that keeps it from being read as sealed.
    /// </remarks>
    public static string Flatten(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return " ";

        var chars = new char[raw.Length + 2];
        var length = 0;
        chars[length++] = ' ';

        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c)) chars[length++] = char.ToLowerInvariant(c);
            else if (chars[length - 1] != ' ') chars[length++] = ' ';
        }

        if (chars[length - 1] != ' ') chars[length++] = ' ';
        return new string(chars, 0, length);
    }

    /// <summary>
    /// The band a sold comp's condition field states, or null when it states nothing this
    /// recognises. Null is a real answer and is counted as such — see
    /// <see cref="LiveConditionRead.CoveragePercent"/>.
    /// </summary>
    public static string? FromCompCondition(string? condition)
    {
        var text = Flatten(condition);
        if (text.Trim().Length == 0) return null;

        foreach (var (phrase, band, _) in Vocabulary)
            if (text.Contains($" {Flatten(phrase).Trim()} ", StringComparison.Ordinal)) return band;

        return null;
    }

    /// <summary>
    /// What a lot's own name says it is in, and the words that said it. The <b>worst</b> band wins
    /// when a name states more than one — "tested working, screen cracked" is a cracked screen, and
    /// every rounding on this card goes against the bidder.
    /// </summary>
    public static (string Band, string Evidence) FromTitle(string? title)
    {
        var text = Flatten(title);
        if (text.Trim().Length == 0) return (LiveConditionBands.Unstated, "");

        var band = LiveConditionBands.Unstated;
        var evidence = "";

        foreach (var (phrase, candidate, inATitle) in Vocabulary)
        {
            if (!inATitle) continue;
            var needle = $" {Flatten(phrase).Trim()} ";
            if (!text.Contains(needle, StringComparison.Ordinal)) continue;

            // Worst wins. Rank(unstated) is -1, so the first hit always takes.
            if (band != LiveConditionBands.Unstated && Rank(candidate) >= Rank(band)) continue;
            band = candidate;
            evidence = phrase;
        }

        return (band, evidence);
    }

    /// <summary>
    /// What the seller picked, normalised. Anything unrecognised — including empty, which is the
    /// usual case — is <c>unstated</c> and hands the question back to the lot's name.
    /// </summary>
    public static string FromSeller(string? picked)
    {
        var text = Flatten(picked).Trim();
        if (text.Length == 0) return LiveConditionBands.Unstated;

        return text switch
        {
            LiveConditionBands.New or "sealed" => LiveConditionBands.New,
            LiveConditionBands.LikeNew or "like new" or "open box" or "openbox" => LiveConditionBands.LikeNew,
            LiveConditionBands.Used or "pre owned" or "preowned" => LiveConditionBands.Used,
            LiveConditionBands.Broken or "parts" or "for parts" => LiveConditionBands.Broken,
            _ => LiveConditionBands.Unstated,
        };
    }

    // ── The read ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Read the condition for one live lot. Never null and never throws: comps that state nothing
    /// come back as a read that says so, which is what lets the card carry this block on every card
    /// rather than only when it has bad news.
    /// </summary>
    /// <param name="title">The lot's name as typed. Read only when the seller has not picked.</param>
    /// <param name="picked">What the seller chose in the condition box, if anything. Outranks the
    /// name, including a choice that agrees with it — they are looking at the item.</param>
    /// <param name="comps">The sold rows already in hand. Never re-read, never re-fetched.</param>
    public static LiveConditionRead Read(
        string? title, string? picked, IEnumerable<MarketplaceComparableResult>? comps)
    {
        var rows = comps as IReadOnlyList<MarketplaceComparableResult> ?? comps?.ToList() ?? [];

        var chosen = FromSeller(picked);
        var (fromTitle, evidence) = FromTitle(title);

        var read = new LiveConditionRead
        {
            Band = chosen != LiveConditionBands.Unstated ? chosen : fromTitle,
            Source = chosen != LiveConditionBands.Unstated
                ? LiveConditionSources.Seller
                : (fromTitle != LiveConditionBands.Unstated
                    ? LiveConditionSources.Title
                    : LiveConditionSources.Unstated),
            Evidence = chosen != LiveConditionBands.Unstated ? "" : evidence,
            TotalComps = rows.Count,
        };
        read.BandLabel = Label(read.Band);

        Bucket(read, rows);
        Describe(read);
        return read;
    }

    /// <summary>
    /// The comps, split by the condition they stated. Prices are <see cref="MarketplaceComparableResult.SoldPrice"/>
    /// and the median is <see cref="MarketplacePricingCalculator.Median"/> — the same field and the
    /// same function the price estimator's own median is built from, so the ratio between two bands
    /// is a fact about condition and not about two different ways of averaging.
    /// </summary>
    private static void Bucket(LiveConditionRead read, IReadOnlyList<MarketplaceComparableResult> rows)
    {
        var byBand = new Dictionary<string, List<decimal>>(StringComparer.Ordinal);
        var all = new List<decimal>();

        foreach (var row in rows)
        {
            if (row.SoldPrice <= 0m) continue;
            if (FromCompCondition(row.Condition) is not { } band) continue;

            if (!byBand.TryGetValue(band, out var prices)) byBand[band] = prices = [];
            prices.Add(row.SoldPrice);
            all.Add(row.SoldPrice);
        }

        read.ClassifiedComps = all.Count;
        read.CoveragePercent = rows.Count == 0
            ? 0m
            : Math.Round(all.Count * 100m / rows.Count, 1);

        if (all.Count == 0) return;

        read.AllMedian = Math.Round(MarketplacePricingCalculator.Median(all), 2);
        read.Bands = byBand
            .Select(kv => new LiveConditionBandRead
            {
                Band = kv.Key,
                Label = Label(kv.Key),
                Count = kv.Value.Count,
                Median = Math.Round(MarketplacePricingCalculator.Median(kv.Value), 2),
                SharePercent = Math.Round(kv.Value.Count * 100m / all.Count, 1),
                IsThisLot = kv.Key == read.Band,
            })
            .OrderByDescending(b => Rank(b.Band))
            .ToList();

        read.Mixed = read.Bands.Count > 1;
        var dominant = read.Bands.OrderByDescending(b => b.Count).ThenByDescending(b => Rank(b.Band)).First();
        read.DominantBand = dominant.Band;
        read.DominantLabel = dominant.Label;

        var matched = read.Bands.FirstOrDefault(b => b.Band == read.Band);
        read.MatchedComps = matched?.Count ?? 0;
        read.MatchedMedian = matched?.Median ?? 0m;

        read.Readable = read.CoveragePercent >= MinCoveragePercent && all.Count >= MinClassifiedComps;
    }

    // ── The words, and what the reading is allowed to do to the ceiling ───────────────────────

    /// <summary>
    /// The headline, the money sentence and — only where the seller has to act — the warning.
    /// Every path sets a money sentence, including the ones that changed nothing: a block that only
    /// speaks when it took money off is a block whose silence means both "the comps are the right
    /// condition" and "nothing looked".
    /// </summary>
    private static void Describe(LiveConditionRead read)
    {
        var stated = read.Band != LiveConditionBands.Unstated;
        var lot = ShortLabel(read.Band);

        read.Headline = stated
            ? $"Bidding on a {lot} one"
            : "Condition not stated";

        if (!read.Readable)
        {
            read.Headline += read.ClassifiedComps > 0
                ? $" — only {read.ClassifiedComps} of {read.TotalComps} sold comps say what condition they were in"
                : " — the sold comps don't say what condition they were in";
            read.MoneyNote =
                "The ceiling below is priced off every matching sale, whatever condition each one was in. " +
                "Not enough of these rows state a condition to split them, so nothing was re-priced.";
            return;
        }

        var mix = string.Join(", ", read.Bands.Select(b => $"{b.Count} {ShortLabel(b.Band)} at {b.Median:C0}"));

        read.Headline = stated
            ? $"Bidding on a {lot} one · comps: {mix}"
            : $"Condition not stated · comps: {mix}";

        // Nothing said what this is. The comps are not asked to guess — they are shown, so the
        // seller can see what answering is worth. It is the most actionable state on this block and
        // the only one where a single keystroke changes the ceiling.
        if (!stated)
        {
            read.MoneyNote = read.Mixed
                ? "The ceiling below is priced off all of them together — sealed and used in one median, " +
                  "which is the right price for neither. Set Condition and it re-prices off the matching " +
                  "sales instantly, with no second eBay read."
                : $"Every one of these sold as {ShortLabel(read.DominantBand)}, so the ceiling below is " +
                  $"already a {ShortLabel(read.DominantBand)} price.";

            if (read.Mixed && Spread(read) is { } spread)
            {
                read.Warning =
                    $"Nothing says what condition this lot is in. The sales behind the ceiling run " +
                    $"{spread.Low.Median:C0} for {ShortLabel(spread.Low.Band)} to {spread.High.Median:C0} for " +
                    $"{ShortLabel(spread.High.Band)} — the ceiling is priced off the middle of that. " +
                    "Set Condition to the one you are looking at.";
            }
            return;
        }

        // Every classified comp is already in this lot's band. Nothing to cut, and worth saying —
        // it is the one state where the ceiling is provably the right kind of price.
        if (!read.Mixed && read.MatchedComps > 0)
        {
            read.MoneyNote = $"Every one of these {read.ClassifiedComps} sales was {lot} too, so the ceiling " +
                             "below is already priced on the right condition.";
            return;
        }

        if (read.MatchedComps < MinBandComps)
        {
            var better = Rank(read.DominantBand) > Rank(read.Band);
            // "0 used sales are in here" is a sentence about arithmetic. "No used sale is in here"
            // is the fact, and it is the sharper one.
            var howMany = read.MatchedComps == 0
                ? $"No {lot} sale is"
                : $"Only {read.MatchedComps} {lot} {(read.MatchedComps == 1 ? "sale is" : "sales are")}";

            read.MoneyNote =
                $"The ceiling below is priced off all {read.ClassifiedComps} classified sales, most of them " +
                $"{ShortLabel(read.DominantBand)}. {howMany} in here — under {MinBandComps} there is " +
                "no band median worth pricing against, so nothing was cut.";

            // The one case where the badge is knowingly optimistic: the thing on screen is in worse
            // shape than nearly everything it was priced off, and there is no honest number to
            // correct it by. Saying so is the whole value here.
            if (better)
            {
                read.Warning =
                    $"This is a {lot} one and the comps behind the ceiling are mostly " +
                    $"{ShortLabel(read.DominantBand)} — {read.Bands.First(b => b.Band == read.DominantBand).Count} " +
                    $"of {read.ClassifiedComps}. " +
                    (read.MatchedComps == 0
                        ? $"There is no {lot} sale to price off at all"
                        : $"Only {read.MatchedComps} {lot} {(read.MatchedComps == 1 ? "sale" : "sales")} to price off") +
                    ", which is too few to cut the ceiling with — so treat the number above as a " +
                    $"{ShortLabel(read.DominantBand)} price and bid well under it.";
            }
            return;
        }

        // Enough matching sales to price on. The band's own median against the median of everything
        // classified — measured, not assumed.
        if (read.AllMedian <= 0m || read.MatchedMedian <= 0m)
        {
            read.MoneyNote = "The ceiling below is priced off the whole sold history — these rows carry no " +
                             "price to split by condition.";
            return;
        }

        if (read.MatchedMedian >= read.AllMedian)
        {
            // Deliberately no upside. See the class remarks: on a screen with seconds and one
            // hammer, paying today for a condition claimed by the person selling it is how a good
            // read loses money.
            read.MoneyNote =
                $"These {lot} ones actually sold for MORE than the mixed median — {read.MatchedMedian:C0} " +
                $"against {read.AllMedian:C0}. The ceiling below is left at the mixed price: a better condition " +
                "never raises it, because the condition is a claim about an item you are seeing through a camera.";
            return;
        }

        var raw = read.MatchedMedian / read.AllMedian;
        var floor = 1m - MaxHaircutPercent / 100m;
        read.ResaleMultiplier = Math.Round(Math.Max(floor, raw), 4);
        read.Discounted = true;
        read.CutPercent = Math.Round((1m - read.ResaleMultiplier) * 100m, 1);
        read.Floored = raw < floor;

        read.MoneyNote =
            $"The ceiling below is priced {read.CutPercent:0.#}% under the mixed median, on what the " +
            $"{read.MatchedComps} {lot} ones actually fetched — {read.MatchedMedian:C0} against " +
            $"{read.AllMedian:C0} across every condition." +
            (read.Floored
                ? $" The gap measured wider than that; the cut stops at {MaxHaircutPercent:0}%, because past " +
                  "there it stops looking like the same item in worse shape."
                : "");

        read.Warning =
            $"This is a {lot} one and the comps are mixed. {ShortLabel(read.DominantBand)} ones fetch " +
            $"{read.Bands.First(b => b.Band == read.DominantBand).Median:C0}; the {read.MatchedComps} {lot} " +
            $"ones fetched {read.MatchedMedian:C0}. The ceiling below is cut {read.CutPercent:0.#}% to match — " +
            "the middle-half spread and the comp list are still every sale, whatever condition it was in.";
    }

    /// <summary>The best- and worst-paying bands in the comps, when there is more than one. What
    /// makes "condition decides which end you get" a pair of dollar figures rather than a maxim.</summary>
    private static (LiveConditionBandRead Low, LiveConditionBandRead High)? Spread(LiveConditionRead read)
    {
        if (read.Bands.Count < 2) return null;
        var ordered = read.Bands.OrderBy(b => b.Median).ToList();
        return (ordered[0], ordered[^1]);
    }

    // ── Handing the cut to the money ─────────────────────────────────────────────────────────

    /// <summary>
    /// The same resale figures scaled to the lot's own condition band, or the original object when
    /// nothing was cut.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same shape as <see cref="LiveTrend.Discount"/>, and composed with it rather than
    /// competing: the trend ratio is what these have been fetching <i>lately</i> and this one is
    /// what they fetch <i>in this condition</i>. Both are ratios measured off the same rows, so
    /// applying one to the other is the two facts stacked, not one number overwriting another.
    /// </para>
    /// <para>
    /// Only the three prices the ceiling is built out of move. The percentile spread, the comp
    /// table, the sell-through and the confidence stay exactly as they were — those describe sales
    /// that really happened, and scaling them would be inventing sales nobody made. Returning the
    /// <b>same instance</b> when nothing was cut is what makes "a card with no condition read is
    /// priced exactly as it was before this existed" a property of the code.
    /// </para>
    /// </remarks>
    public static ResalePricing Discount(ResalePricing resale, LiveConditionRead? condition)
    {
        if (condition is not { Discounted: true }) return resale;

        var multiplier = condition.ResaleMultiplier;
        if (multiplier <= 0m || multiplier >= 1m) return resale;

        return new ResalePricing
        {
            LookupTitle = resale.LookupTitle,
            Median = Scale(resale.Median, multiplier),
            ExpectedSale = Scale(resale.ExpectedSale, multiplier),
            QuickSale = Scale(resale.QuickSale, multiplier),
            SoldCompCount = resale.SoldCompCount,
            TerapeakCompCount = resale.TerapeakCompCount,
            SoldCompWeightPercent = resale.SoldCompWeightPercent,
            TerapeakWeightPercent = resale.TerapeakWeightPercent,
            PricedCompCount = resale.PricedCompCount,
            IdentityVerified = resale.IdentityVerified,
            AvgCompShipping = resale.AvgCompShipping,
            ConfidenceScore = resale.ConfidenceScore,
            ConfidenceLevel = resale.ConfidenceLevel,
            Basis = resale.Basis,
            DisagreementMessage = resale.DisagreementMessage,
            LiquidityScore = resale.LiquidityScore,
            LiquidityLevel = resale.LiquidityLevel,
            EstimatedDaysToSell = resale.EstimatedDaysToSell,
            EstimatedMonthlySales = resale.EstimatedMonthlySales,
            OpportunityScore = resale.OpportunityScore,
        };
    }

    private static decimal? Scale(decimal? price, decimal multiplier) =>
        price is > 0m ? Math.Round(price.Value * multiplier, 2) : price;
}
