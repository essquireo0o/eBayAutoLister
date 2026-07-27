using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// What remaining cover is worth on the resale, and what it stops being at risk on the buy. Pure,
/// and small on purpose: the profit maths is the shared <see cref="ProfitCalculator"/> — this only
/// decides the resale price to hand it, and the sentence explaining where that price came from.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place in the app where a listing's own prose is allowed to raise a resale
/// estimate above what the sold comps produced, and that deserves an argument rather than an
/// assertion. The argument is: the board already trusts the listing's price completely. A row that
/// says "$250" is costed at $250 with no corroboration whatsoever. Reading "still under manufacturer
/// warranty until March 2027" from the same sentence and adding a capped few percent is strictly
/// less credulous than what the board already does — and it is a premium the reseller can actually
/// realise, because "still under warranty until March 2027" is a line they can put in their own eBay
/// listing.
/// </para>
/// <para>
/// It is fenced anyway, on four sides, because being right about this on average is not the same as
/// being right about the row somebody is about to drive across town for:
/// </para>
/// <list type="number">
///   <item><b>Only stated cover counts.</b> An <see cref="WarrantyEvidence.Estimated"/> reading — from a
///   purchase date, or an unopened box — is worth exactly nothing, however plausible.</item>
///   <item><b>Only transferable cover counts.</b> A seller's own 30-day promise, and a DeWalt term
///   that names the original purchaser, protect the reseller and are worth nothing to whoever the
///   reseller sells to.</item>
///   <item><b>Only believable prices get a premium.</b> Under
///   <see cref="WarrantySelectors.MinUpliftComps"/> sold comps or
///   <see cref="WarrantySelectors.MinUpliftConfidence"/> confidence, a percentage on top of the
///   estimate is two guesses stacked on each other.</item>
///   <item><b>Both caps apply.</b> <see cref="WarrantySelectors.MaxUpliftPercent"/> and then
///   <see cref="WarrantySelectors.MaxUpliftDollars"/>, so a long warranty on an expensive item cannot
///   put a three-figure sum of unverified prose into the profit ranking.</item>
/// </list>
/// <para>
/// When a fence bites the row's money is exactly what it would have been without this feature, and
/// <see cref="WarrantyEconomics.HeldBackReason"/> says which fence and why. Nothing is quietly
/// dropped.
/// </para>
/// </remarks>
public static class WarrantyPricer
{
    /// <summary>
    /// What the cover does to this row. <paramref name="expectedSale"/> is the comps' own answer, and
    /// what comes back says how much — if anything — should be added to it.
    /// </summary>
    /// <param name="buyCostAllIn">
    /// What the reseller actually spends. Not used in the uplift; used to say how much of their own
    /// money the cover protects, which is the half of this feature that has nothing to do with price.
    /// </param>
    /// <param name="allowUplift">
    /// False on rows where a per-unit warranty cannot honestly move the number — an auction lot,
    /// whose price is its grade times its unit count, and a row with no resale estimate to add to.
    /// The cover is still read, still shown and still counted as protection on the buy; only the
    /// premium is refused, and the row says which of the two happened.
    /// </param>
    public static WarrantyEconomics Value(
        WarrantyDetails details, decimal expectedSale, decimal buyCostAllIn, int compCount, int confidenceScore,
        bool allowUplift = true)
    {
        var economics = new WarrantyEconomics
        {
            Kind = details.Kind,
            Evidence = details.Evidence,
            KindLabel = details.KindLabel,
            ProgramLabel = details.ProgramLabel,
            ConditionLabel = details.ConditionLabel,
            SourceText = details.SourceText,
            MonthsRemaining = details.MonthsRemaining,
            ExpiresUtc = details.ExpiresUtc,
            ExpiresText = ExpiryText(details),
            TransfersToBuyer = details.TransfersToBuyer,
            ResaleWithoutWarranty = expectedSale,
            ResaleWithWarranty = expectedSale,
            // Every kind except a stated absence protects the money the reseller is about to spend —
            // including the ones worth nothing on resale. A seller's 30-day promise is the difference
            // between a dead unit costing the purchase price and costing a drive back.
            CoversYourBuy = details.Kind != WarrantyKinds.None && details.MonthsRemaining is > 0,
        };

        economics.ProtectedCost = economics.CoversYourBuy ? Math.Round(buyCostAllIn, 2) : 0m;

        var held = allowUplift
            ? HoldBackReason(details, compCount, confidenceScore)
            : expectedSale > 0m
                ? "An auction lot is priced by its grade and its unit count, so one unit's paperwork can't move the lot's number."
                : "Nothing here has a sold-comp price to add a warranty premium to.";
        if (held is not null)
        {
            economics.HeldBackReason = held;
            economics.Note = NoteFor(economics, details);
            economics.RiskNote = RiskNote(details, buyCostAllIn);
            return economics;
        }

        var percent = UpliftPercentFor(details.MonthsRemaining!.Value);
        // The percentage cap first, then the dollar cap on what it came to. Both, in that order, so
        // the figure is small on a cheap item because the percentage is small and small on an
        // expensive one because the dollar ceiling caught it.
        var uplift = Math.Round(expectedSale * Math.Min(percent, WarrantySelectors.MaxUpliftPercent) / 100m, 2);
        uplift = Math.Min(uplift, WarrantySelectors.MaxUpliftDollars);

        economics.UpliftPercent = expectedSale > 0m ? Math.Round(uplift / expectedSale * 100m, 1) : 0m;
        economics.ResaleUplift = uplift;
        economics.ResaleWithWarranty = Math.Round(expectedSale + uplift, 2);
        economics.Note = NoteFor(economics, details);
        return economics;
    }

    /// <summary>
    /// Why this row's warranty earned nothing, or null when it earned its uplift. Ordered so the
    /// seller is told the most actionable reason first — "go and ask when they bought it" is worth
    /// more than "the comps are thin".
    /// </summary>
    public static string? HoldBackReason(WarrantyDetails details, int compCount, int confidenceScore)
    {
        if (details.Kind == WarrantyKinds.None)
            return "The listing says there is no cover, so there is nothing to add.";

        if (details.MonthsRemaining is null)
        {
            return "The listing states a warranty length but not when it started, so how much is " +
                   "left is anyone's guess — ask for the purchase date or the receipt.";
        }

        if (details.MonthsRemaining < WarrantySelectors.MinCreditedMonths)
        {
            return details.MonthsRemaining == 0
                ? "The cover has already run out."
                : "Under a month left — a fact about the item rather than something a buyer pays for.";
        }

        if (details.Evidence == WarrantyEvidence.Estimated)
        {
            return "This is worked out from what the listing said about the item's age, not from a " +
                   "warranty it claimed — so it is worth asking about and worth nothing on the price.";
        }

        if (!details.TransfersToBuyer)
        {
            return details.Kind == WarrantyKinds.Seller
                ? "The seller's own guarantee covers YOU, not the person you sell it to — worth having, worth nothing on resale."
                : "This brand's cover names the original purchaser, so it protects your buy and can't be advertised to your buyer.";
        }

        if (compCount < WarrantySelectors.MinUpliftComps)
        {
            return $"Only {compCount} sold comp{(compCount == 1 ? "" : "s")} behind the resale price — " +
                   "too thin a base to add a premium on top of.";
        }

        if (confidenceScore < WarrantySelectors.MinUpliftConfidence)
            return "The sold data behind this price is weak, and a premium on top of a shaky estimate is two guesses.";

        return null;
    }

    /// <summary>
    /// What remaining cover adds to a resale price, as a percentage of it.
    /// </summary>
    /// <remarks>
    /// Flat bands rather than a curve, because the underlying effect is a step function in the
    /// buyer's head: "it's still under warranty" is worth a lot more than "it isn't", and two years
    /// left is worth barely more than one. The top band is deliberately at
    /// <see cref="WarrantySelectors.MaxUpliftPercent"/> so the ceiling is reached by the ordinary
    /// good case rather than only by an exotic one.
    /// </remarks>
    public static decimal UpliftPercentFor(int monthsRemaining) => monthsRemaining switch
    {
        >= 12 => 10m,
        >= 6 => 7m,
        >= 3 => 4m,
        >= 1 => 2m,
        _ => 0m,
    };

    /// <summary>
    /// The one sentence the row shows. Says what the cover is, how long is left, and what that was —
    /// or was not — worth, in this row's own dollars.
    /// </summary>
    private static string NoteFor(WarrantyEconomics economics, WarrantyDetails details)
    {
        if (details.Kind == WarrantyKinds.None)
        {
            return "Sold with no cover — if it's dead on arrival, the whole buy price is the loss. " +
                   "Test it in front of them before any money changes hands.";
        }

        var what = details.Kind switch
        {
            WarrantyKinds.Refurbisher => economics.ProgramLabel.Length > 0
                ? $"{economics.ProgramLabel} cover" : "Refurbisher cover",
            WarrantyKinds.Extended => "A protection plan",
            WarrantyKinds.Seller => "The seller's own guarantee",
            _ => "Factory cover",
        };

        var howLong = economics.ExpiresText.Length > 0 ? $" {economics.ExpiresText}" : "";
        var proof = details.HasProofOfPurchase
            ? " The listing mentions a receipt — get it, because a claim without one is a conversation."
            : " Ask for the receipt before you pay: without proof of purchase a claim is a conversation.";

        if (economics.ResaleUplift > 0m)
        {
            return $"{what}{howLong}, and it transfers — worth about {Money(economics.ResaleUplift)} more on " +
                   $"resale ({economics.UpliftPercent:0.#}%), which is already in the profit above. " +
                   $"Your {Money(economics.ProtectedCost)} is covered too.{proof}";
        }

        var protectedNote = economics.CoversYourBuy
            ? $" It still covers the {Money(economics.ProtectedCost)} you're spending, which is the point of buying this one over an identical uncovered unit."
            : "";

        return $"{what}{howLong}. {economics.HeldBackReason}{protectedNote}".Trim();
    }

    /// <summary>
    /// The warning on a row the listing states is uncovered — and only when the buy is big enough for
    /// that to change a decision. Null everywhere else.
    /// </summary>
    /// <remarks>
    /// Below <see cref="WarrantySelectors.AsIsRiskThreshold"/> the whole outlay is smaller than one
    /// bad flip's shipping, and a warning that fires on every $30 row on a classifieds board is a
    /// warning nobody reads by the time it matters.
    /// </remarks>
    public static string? RiskNote(WarrantyDetails details, decimal buyCostAllIn)
    {
        if (details.Kind != WarrantyKinds.None) return null;
        if (buyCostAllIn < WarrantySelectors.AsIsRiskThreshold) return null;

        return $"{Money(buyCostAllIn)} with no cover and no returns — if it doesn't work, that is the loss, " +
               "in full. Test it before you hand over the money.";
    }

    /// <summary>
    /// The verdict, corrected for cover. The uplift has already been through the profit maths by the
    /// time this runs, so this only handles what money cannot express.
    /// </summary>
    /// <remarks>
    /// Only ever LOWERS a verdict, and only on the one case where the arithmetic is confidently
    /// describing the wrong risk: an expensive item the listing states is sold as-is with no returns.
    /// The profit on that row may well be real — and a green badge on it tells the seller to commit
    /// four figures to a stranger's word about a thing they cannot return. Same posture, and the same
    /// reason, as <see cref="LocalArbitrageAnalyzer.JudgeFreebie"/>: every correction there lowers a
    /// verdict too.
    /// </remarks>
    public static (string Verdict, string Note) JudgeWarranty(
        string verdict, string note, WarrantyEconomics economics)
    {
        // Nothing to hold back on a row that already says walk away.
        if (verdict is "pass" or "no_data") return (verdict, note);

        if (economics.RiskNote is not { } risk) return (verdict, note);

        // The money is not in dispute; the exposure is. A goldmine badge is an instruction to go and
        // buy it, and this is the one row where that instruction should come with a hand on the arm.
        return verdict == "goldmine" ? ("solid", $"{note} {risk}") : (verdict, $"{note} {risk}");
    }

    /// <summary>How long is left, said the way a person would rather than as a month count.</summary>
    public static string ExpiryText(WarrantyDetails details)
    {
        var months = details.MonthsRemaining;
        var until = details.ExpiresUtc is DateTime end ? $" (to {WarrantyDetector.MonthYear(end)})" : "";

        return months switch
        {
            null => details.TermMonths > 0 ? $"— a {TermText(details.TermMonths)} term, start date unstated" : "",
            0 => "— expired",
            1 => $"with about a month left{until}",
            { } m => $"with about {m} months left{until}",
        };
    }

    private static string TermText(int months) =>
        months % 12 == 0 && months >= 12
            ? $"{months / 12}-year"
            : $"{months}-month";

    private static string Money(decimal value) => $"${value:0.##}";
}
