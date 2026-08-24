using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>What the metal content did to one row's resale price, and the sentence explaining it.</summary>
public enum MeltOutcome
{
    /// <summary>Nothing could price it and the metal did.</summary>
    Priced,
    /// <summary>Comps priced it BELOW its own metal, so the metal took over. The bug this exists for.</summary>
    Raised,
    /// <summary>Comps came out above melt — a premium over the metal, which is normal. Melt is stated as the floor.</summary>
    Floor,
    /// <summary>The ask contradicts the title's own metal. No price is taken from either; the row says so.</summary>
    Contradicted,
}

/// <summary>The melt anchor's decision about one product. Pure data — <see cref="MeltAnchor"/> makes it.</summary>
/// <param name="Pricing">
/// What the row should be priced from. The melt pricing on <see cref="MeltOutcome.Priced"/> and
/// <see cref="MeltOutcome.Raised"/>; the comps pricing untouched on the other two — melt is a floor
/// under a price, never a ceiling over one.
/// </param>
public sealed record MeltVerdict(
    MeltOutcome Outcome, MetalMelt Melt, ResalePricing? Pricing, string Tier, string Note)
{
    /// <summary>True when this verdict replaced what the comps said, rather than annotating it.</summary>
    public bool SetsPrice => Outcome is MeltOutcome.Priced or MeltOutcome.Raised;
}

/// <summary>
/// Prices a lot of metal off the spot price instead of off comps for other lots of metal.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bug.</b> The Opportunity Finder priced a "1 OZ Gold USA 100 Dollar Bullion Bar" at
/// <b>$6.99</b> off two sold comps, because the comps that matched were novelty replicas. The same
/// morning a "natural gold nugget 2.53 gram" was called "resells around $260" off nineteen comps
/// widened to plain "natural gold nugget" — lots running from a tenth of a gram to a hundred grams,
/// whose sale prices have no central tendency at all. Both times the arithmetic was right and the
/// instrument was wrong: comps answer "what do things with these words sell for", and for a
/// commodity the answer is "it depends entirely on how much of it there is".
/// </para>
/// <para>
/// <b>The instrument.</b> A commodity has a published price per gram. Weight × purity × spot is the
/// whole valuation, it is checkable against Google in five seconds, and it does not care what words
/// are in the title. See <see cref="PreciousMetalPricer"/>, which reads the metal and fetches spot;
/// this file decides what to DO with the answer.
/// </para>
/// <para>
/// <b>Why melt is a floor and not a price.</b> A 1909-S VDB cent is worth a thousand times its
/// copper and an American Eagle carries a premium over its gold. So melt only ever takes over when
/// there is no comps price at all, or when the comps price is <i>below</i> the metal — which is the
/// signal that the comps are for a different kind of object. Above melt, the comps stand and the
/// metal is stated underneath them as the downside.
/// </para>
/// <para>
/// <b>The failure this must not cause.</b> Trusting a title is how a $6.99 replica becomes a $4,600
/// goldmine — an error far more expensive than the one being fixed, because it ends with the owner
/// driving somewhere with cash. Nobody sells an ounce of real gold for seven dollars. So the ask is
/// checked against the metal first (<see cref="TooCheapToBeReal"/>): when the price contradicts the
/// title by that much, the title is what is wrong, and this refuses to price the row at all and
/// says why. A genuine bargain — scrap bought at 50-70% of melt, which is what this board exists to
/// find — sits far above that bar and prices normally.
/// </para>
/// </remarks>
public static class MeltAnchor
{
    /// <summary>
    /// Below this fraction of its own melt value, an ask is evidence the title is not describing
    /// solid metal.
    /// </summary>
    /// <remarks>
    /// Set where it is because of what sits on each side. Real scrap and estate lots change hands
    /// at 50-90% of melt and pawn/dealer offers at 45-70% — the whole opportunity this board looks
    /// for lives in that band and must price normally. Below a third, there is no seller and no
    /// market that explains the number: it is a replica, a plated blank, a picture of a bar, or a
    /// weight in the title that belongs to something else in the lot.
    /// </remarks>
    public const decimal TooCheapToBeReal = 0.35m;

    /// <summary>What the metal says about this product, or null when it has nothing to say.</summary>
    /// <param name="lookupTitle">The title the comps lookup ran against — what the row is priced AS.</param>
    /// <param name="melt">The metal content at spot, from <see cref="PreciousMetalPricer.ValueAsync"/>.</param>
    /// <param name="comps">Whatever the sold-comps tiers came back with, price or no price.</param>
    /// <param name="lowestAsk">The cheapest ask across the group — what a buyer would actually pay.</param>
    /// <remarks>Pure and total: no clock, no network, no mutation of anything passed in.</remarks>
    /// <param name="askIsFirm">
    /// Whether <paramref name="lowestAsk"/> is a price somebody is actually ASKING. True on a shelf
    /// or a fixed-price listing, where a $6.99 "1 oz gold bar" tells you the title is a lie. FALSE
    /// on a live auction, where the current bid starts near zero on purpose and says nothing at all
    /// about the lot — reading an opening dollar as a contradiction would refuse to price every real
    /// bullion lot for the first several bids, which is precisely when the seller needs the number.
    /// </param>
    public static MeltVerdict? Decide(string lookupTitle, MetalMelt? melt, ResalePricing? comps, decimal lowestAsk,
                                      bool askIsFirm = true)
    {
        if (melt is null || melt.MeltLow <= 0m) return null;

        var compsPrice = comps is not null && comps.HasPrice
            ? (comps.ExpectedSale is > 0 ? comps.ExpectedSale!.Value : comps.Median ?? 0m)
            : 0m;

        // ── The ask has the first word, because it is the one fact nobody wrote to sell you ──────
        // A free listing gets the same treatment: there is no ask to check the title against, and
        // "free gold" is a category of ad, not a category of metal.
        if (askIsFirm && (lowestAsk <= 0m || lowestAsk < melt.MeltLow * TooCheapToBeReal))
        {
            return new MeltVerdict(MeltOutcome.Contradicted, melt, comps, Tier: "", Note: Contradiction(melt, lowestAsk));
        }

        if (compsPrice <= 0m)
        {
            return new MeltVerdict(MeltOutcome.Priced, melt, Price(lookupTitle, melt), TierFor(melt),
                $"Priced off the metal in it, not off sold listings: {melt.Arithmetic}. "
                + "A commodity is worth what its weight is worth, and that price is published every minute."
                + PurityCaveat(melt) + Unvouched(askIsFirm));
        }

        if (compsPrice < melt.MeltLow)
        {
            return new MeltVerdict(MeltOutcome.Raised, melt, Price(lookupTitle, melt), TierFor(melt),
                $"The sold comps came out at {compsPrice:C2}, below what the metal alone is worth — "
                + $"{melt.Arithmetic}. Comps that price metal under its own weight are comps for a "
                + "different object, so this is priced off spot instead."
                + PurityCaveat(melt) + Unvouched(askIsFirm));
        }

        return new MeltVerdict(MeltOutcome.Floor, melt, comps, Tier: "",
            Note: $"The metal alone is worth {Span(melt)} — {melt.Arithmetic} — so the comps above it are a "
                + "premium for what it is, not just what it weighs. That metal value is the floor under this buy.");
    }

    /// <summary>
    /// Said only where the ask could not be checked. On a shelf, a price far under the metal is the
    /// tell that the title is wrong; at an auction there is no such tell, so the seller is told that
    /// the figure rests on the lot being what it says it is rather than on anything corroborating it.
    /// </summary>
    private static string Unvouched(bool askIsFirm) =>
        askIsFirm ? "" : " Nothing here corroborates the title — a current bid is not an asking price — "
                       + "so this figure assumes the lot is the metal and weight it says it is.";

    /// <summary>A resale price that is the metal, shaped exactly like a comps-priced one.</summary>
    /// <remarks>
    /// The LOW end of the purity range is the price everywhere — expected sale, median and quick
    /// sale alike. Two reasons, and both are the same reason: metal sells at metal, so there is no
    /// spread between "what it fetches" and "what it fetches quickly"; and where purity is a guess
    /// the low end is the only end that cannot be an overstatement. Every comp count stays zero,
    /// which is the truth — this rests on no sold listing at all.
    /// </remarks>
    public static ResalePricing Price(string lookupTitle, MetalMelt melt) => new()
    {
        LookupTitle = lookupTitle,
        Median = melt.MeltLow,
        ExpectedSale = melt.MeltLow,
        QuickSale = melt.MeltLow,
        ConfidenceLevel = melt.PurityIsKnown ? "Metal content" : "Metal content (purity assumed)",
    };

    /// <summary>
    /// Stamps a built row with the melt verdict — the tier, the sentence, and the valuation chip.
    /// </summary>
    /// <remarks>
    /// Called after <c>LocalArbitrageAnalyzer.Build</c> and before <c>Rank</c>, so the ranking sees
    /// the corrected tier. It restamps rather than being folded into the analyzer's own
    /// <c>ApplyEvidence</c> only because that file is held by another session; the rule belongs
    /// there and should move when the lane clears. See .Codex/FROM-CLAUDE.md.
    /// </remarks>
    public static void Apply(LocalArbitrageOpportunity row, MeltVerdict verdict)
    {
        if (verdict.Outcome is MeltOutcome.Contradicted or MeltOutcome.Floor)
        {
            // Neither of these takes the price over, so neither may touch the tier: the comps are
            // still what priced this row and still what its percentages should be believed on. They
            // add the one fact the comps could not know.
            row.EvidenceNote = string.IsNullOrWhiteSpace(row.EvidenceNote)
                ? verdict.Note
                : $"{row.EvidenceNote.TrimEnd()} {verdict.Note}";
            return;
        }

        row.EvidenceTier = verdict.Tier;
        row.EvidenceNote = verdict.Note;
        row.PricedAs = verdict.Melt.Content.Metal + " content";
        // No comp priced this and none is claimed to have. Left at zero rather than carried over
        // from a comps lookup that has just been overruled — a row that says "3 sold comps" beside
        // a price none of them produced is the misreading this whole file exists to stop.
        row.PricedCompCount = 0;
        row.IdentityVerified = true;
        row.ConfidenceLevel = verdict.Pricing?.ConfidenceLevel ?? "Metal content";

        row.Valuation = new ResaleValuation
        {
            Status = ValuationStatuses.Melt,
            ProviderId = ResaleValuationProviders.MetalMelt,
            SourceLabel = verdict.Melt.PurityIsKnown ? "metal content at spot" : "metal content at spot (purity assumed)",
            Confidence = verdict.Tier,
            Note = verdict.Note,
            // The sold search is kept exactly as it was. An estimate the seller can check by hand in
            // one click is a different thing from one they have to take on faith — and here they
            // can check it twice, against the comps and against the spot price.
            LookupQuery = row.Valuation?.LookupQuery ?? row.Title,
            LookupUrl = row.Valuation?.LookupUrl ?? "",
        };
    }

    /// <summary>
    /// Melt with a stated purity is the most certain price in this app; melt with an assumed one is
    /// not.
    /// </summary>
    /// <remarks>
    /// "14k" or ".999" on the item makes weight × purity × spot an arithmetic fact, checkable in
    /// five seconds and resting on a published number rather than on somebody else's sale — so it
    /// is graded <see cref="LocalArbitrageEvidence.Confident"/>, which is what it is. An unmarked
    /// nugget is priced on an assumption about purity, and an assumption is an estimate however
    /// good the arithmetic on top of it.
    /// </remarks>
    public static string TierFor(MetalMelt melt) =>
        melt.PurityIsKnown ? LocalArbitrageEvidence.Confident : LocalArbitrageEvidence.Low;

    private static string PurityCaveat(MetalMelt melt) =>
        melt.PurityIsKnown ? "" : $" {melt.Content.PurityNote} — the figure is the low end, {Span(melt)}.";

    private static string Span(MetalMelt melt) =>
        melt.PurityIsKnown ? $"{melt.MeltLow:C2}" : $"{melt.MeltLow:C2}-{melt.MeltHigh:C2}";

    private static string Contradiction(MetalMelt melt, decimal ask)
    {
        var metal = melt.Content.Metal.ToLowerInvariant();
        var weight = $"{melt.Content.Grams:0.##} g";
        return ask <= 0m
            ? $"The title says {weight} of {metal}, which would be {Span(melt)} of metal at today's spot price. "
              + "This ad states no price, so there is nothing to check that against — and nobody gives metal away. "
              + "Read the listing before you count on the weight."
            : $"The title says {weight} of {metal} — {Span(melt)} of metal at today's spot price — and the ask is "
              + $"{ask:C2}. One of those two is wrong, and it is almost always the title: replicas, plated blanks "
              + "and novelty bars are described exactly this way. Priced off the sold comps, not off the metal.";
    }
}
