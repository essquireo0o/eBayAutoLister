using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Turns one priced row into one word.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is a <b>translation</b>, never a second opinion. The profit, the ROI, the
/// break-even and the verdict tier were all decided by <see cref="LocalArbitrageAnalyzer.Build"/>,
/// which is the same call the Local Deals board, Deal Radar and Roll the Dice make — so a snap and
/// a board row for the same item at the same price cannot disagree. What this adds is the thing a
/// table does not have to answer and a person standing in a driveway does: which single word.
/// </para>
/// <para>
/// The one number it computes for itself is <see cref="PayUpTo"/>, and even that is computed from
/// <see cref="LocalArbitrageAnalyzer"/>'s own public bars rather than a friendlier pair invented
/// here. It is the twin of <see cref="JackpotHunter.TargetBuyPrice"/>, which asks the same question
/// against the goldmine bar; this asks it against the "worth doing" one, because the seller is
/// holding the thing and the realistic question is not "is this a jackpot" but "what do I offer".
/// </para>
/// </remarks>
public static class SnapJudge
{
    /// <summary>
    /// The most to pay and still clear the app's own "worth doing" bar — both halves of it, so the
    /// number satisfies the ROI test and the dollar test at once.
    /// </summary>
    /// <param name="breakEvenBuyPrice">
    /// The price at which this flip nets exactly zero (<see cref="LocalArbitrageOpportunity.MaxBuyPrice"/>).
    /// </param>
    /// <remarks>
    /// Below break-even, every dollar not spent is a dollar of profit, so net profit at a price
    /// <c>p</c> is <c>breakEven - p</c> and ROI is <c>(breakEven - p) / p</c>. Solving each bar for
    /// <c>p</c> and taking the tighter of the two gives one price that passes both. Floored to the
    /// cent rather than rounded: a number quoted as safe has to be safe at the number quoted.
    /// </remarks>
    public static decimal PayUpTo(decimal breakEvenBuyPrice)
    {
        if (breakEvenBuyPrice <= 0m) return 0m;

        var byRoi = breakEvenBuyPrice / (1m + LocalArbitrageAnalyzer.SolidRoiPercent / 100m);
        var byProfit = breakEvenBuyPrice - LocalArbitrageAnalyzer.SolidProfit;
        var target = Math.Min(byRoi, byProfit);

        return target <= 0m ? 0m : Math.Floor(target * 100m) / 100m;
    }

    /// <summary>
    /// The verdict, the sentence and the money, from a row the analyzer has already priced.
    /// </summary>
    /// <param name="askWasKnown">
    /// False when nobody has named a price. The row was still costed — against a zero — so its
    /// profit and ROI are arithmetic about a price the seller has not been offered, and publishing
    /// them as "your profit" would be the most flattering lie this screen could tell. They are
    /// dropped, and the answer becomes the price to stop at instead of a judgement on one.
    /// </param>
    public static SnapResult Build(LocalArbitrageOpportunity row, bool askWasKnown)
    {
        var result = new SnapResult
        {
            Item = row.Title,
            PricedAs = row.PricedAs,
            ImageUrl = row.ImageUrl,
            SourceUrl = row.Url,
            CategoryLabel = row.CategoryLabel,
            AskPrice = askWasKnown ? row.LocalAsk : null,
            AskWasKnown = askWasKnown,
            ResalePrice = row.EbayExpectedSale ?? row.EbayResaleMedian,
            QuickSalePrice = row.EbayQuickSale,
            CompCount = row.PricedCompCount > 0 ? row.PricedCompCount + row.TerapeakCompCount
                                                : row.SoldCompCount + row.TerapeakCompCount,
            ConfidenceScore = row.ConfidenceScore,
            ConfidenceLevel = row.ConfidenceLevel,
            EvidenceTier = row.EvidenceTier,
            EvidenceNote = row.EvidenceNote,
            IdentityVerified = row.IdentityVerified,
            DaysToSell = row.DaysToSell,
            DaysToCash = row.DaysToCash,
            SpeedLabel = row.SpeedLabel,
        };

        // Nothing priced it. Say which of the two reasons applies — the analyzer already wrote that
        // sentence, and "no comps matched this title" and "the comps for this truck are tow hitches"
        // send the seller in completely different directions.
        if (row.Verdict == "no_data" || result.ResalePrice is not > 0m)
        {
            result.Call = SnapCalls.Unknown;
            result.CallLabel = "CAN'T PRICE IT";
            result.Reason = row.VerdictNote.Length > 0
                ? row.VerdictNote
                : "No eBay sold history matched this item, so there is no resale price to check the ask against.";
            return result;
        }

        var breakEven = row.MaxBuyPrice ?? 0m;
        result.BuyMax = breakEven > 0m ? breakEven : null;
        result.PayUpTo = breakEven > 0m ? PayUpTo(breakEven) : null;
        result.ProfitAtPayUpTo = result.PayUpTo is > 0m ? Math.Round(breakEven - result.PayUpTo.Value, 2) : null;

        if (askWasKnown)
        {
            result.NetProfit = row.NetProfit;
            result.RoiPercent = row.RoiPercent;
            result.Fees = row.EstimatedFees;
            result.ShipCost = row.EstimatedShipCost;
            result.Call = CallFor(row.Verdict);
            result.CallLabel = LabelFor(result.Call);
            result.Reason = row.VerdictNote;
        }
        else
        {
            BuildUnpricedAnswer(result, breakEven);
        }

        return result;
    }

    // No price named yet — the yard-sale case, and the one the rest of this app has never had an
    // answer for. Every board in it starts from an ask; here there isn't one, and the useful output
    // is the offer to make rather than a verdict on a number nobody has said out loud.
    private static void BuildUnpricedAnswer(SnapResult result, decimal breakEven)
    {
        var payUpTo = result.PayUpTo ?? 0m;

        if (payUpTo <= 0m)
        {
            result.Call = SnapCalls.Pass;
            result.CallLabel = "PASS";
            // Two different failures, and the difference is worth a sentence: one is worth nothing,
            // the other is worth something but not worth an evening.
            result.Reason = breakEven <= 0m
                ? "Even free, this doesn't clear eBay's cut and the cost of shipping it."
                : $"Only {breakEven:C} of headroom even if they hand it to you — not worth fetching, " +
                  "photographing, listing and packing.";
            return;
        }

        result.Call = SnapCalls.BuyUnder;
        result.CallLabel = $"BUY UNDER {payUpTo:C0}";
        result.Reason =
            $"Sells for about {result.ResalePrice:C} on eBay. Pay up to {payUpTo:C} and you clear " +
            $"{result.ProfitAtPayUpTo:C} after fees and shipping; above {breakEven:C} you lose money.";
    }

    // The analyzer's four tiers, collapsed to the three answers a person can act on. "thin" is not
    // rounded up to BUY and not down to PASS, because it is genuinely neither: the money is real
    // and either the margin or the evidence under it is not.
    private static string CallFor(string verdict) => verdict switch
    {
        "goldmine" or "solid" => SnapCalls.Buy,
        "thin" => SnapCalls.Close,
        "pass" => SnapCalls.Pass,
        _ => SnapCalls.Unknown,
    };

    private static string LabelFor(string call) => call switch
    {
        SnapCalls.Buy => "BUY",
        SnapCalls.Close => "CLOSE CALL",
        SnapCalls.Pass => "PASS",
        _ => "CAN'T PRICE IT",
    };

    // Nothing about the COMPS goes in Warnings. EvidenceTier and EvidenceNote already carry that,
    // in the analyzer's own words and with a tier the UI colours on — and a browser pass showed what
    // adding it here produced: the same sentence rendered twice on one card, once in the evidence
    // strip and once as a bullet under it. A caveat repeated is a caveat discounted, and the card
    // already carries a "See the sold listings" button, which is the action the second copy was
    // spelling out. Warnings is for what the evidence line CANNOT say: what the page failed to
    // publish, and what a photo cannot tell you.

    /// <summary>
    /// The photo's own caveats, which are not the comps' caveats and must not be run together with
    /// them. A confident price for the wrong product is the failure this catches.
    /// </summary>
    public static void AddIdentityWarnings(SnapResult result, SnapIdentity identity)
    {
        if (identity.Certainty.Equals("low", StringComparison.OrdinalIgnoreCase))
        {
            result.Warnings.Add(
                $"Read off a photo, and not confidently — everything below is priced as \"{identity.Title}\". " +
                "Fix the name if that's wrong and price it again.");
        }

        if (identity.CheckThis.Length > 0)
            result.Warnings.Add($"Before you pay: {identity.CheckThis}");
    }
}
