using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Why the app can join a listing it has just published to what the item cost — and when it must
/// refuse to.
/// </summary>
/// <remarks>
/// <para>
/// The gap this closes: a lot won on a live show becomes a draft carrying a SKU and a deal card
/// carrying the cash, and then the seller publishes. Until now the cost sat on the board waiting for
/// somebody to drag the card to <b>Listed</b> and type the eBay listing ID into it by hand. Nobody
/// does that at midnight, so <see cref="CostBasisStore"/> stayed empty, Inventory Health had no
/// break-even floor, and Money Made counted the whole sale price as profit.
/// </para>
/// <para>
/// The listing ID that was missing is minted by the publish. The SKU on the draft is the seller's
/// own and reaches eBay unchanged (see <see cref="SellerSku"/>). Those two facts are both in hand at
/// the same instant exactly once — the moment a publish succeeds — which is where this decides.
/// </para>
/// <para><b>What it refuses to do:</b></para>
/// <list type="bullet">
/// <item><description><b>Guess which deal.</b> Only an exact SKU match counts. No title matching, no
/// "the most recent card" — a cost written against the wrong item is worse than no cost, because it
/// is wrong silently and every profit figure downstream inherits it.</description></item>
/// <item><description><b>Overwrite a number the seller entered.</b> A cost basis that already exists
/// for this listing or SKU stands. The seller typing what they paid is the most reliable input this
/// app has, and a bookkeeping convenience that overwrote it would be a bug that only shows up as a
/// wrong profit months later.</description></item>
/// <item><description><b>Choose between two cards.</b> Two deals under one SKU means the SKU is not
/// the key it is being used as, and picking the first is picking arbitrarily. It reports what it
/// found and writes nothing.</description></item>
/// <item><description><b>Move a card backwards.</b> Sold stays sold, dropped stays dropped. Only a
/// card that has not reached Listed is advanced, because publishing IS the thing Listed means.</description></item>
/// <item><description><b>Fail a publish.</b> Nothing here is allowed to be the reason a live listing
/// reports an error. The listing is already on eBay by the time this runs; this is bookkeeping, and
/// bookkeeping that throws must be caught and said, not raised.</description></item>
/// </list>
/// <para>Pure and static: it decides, and the caller writes.</para>
/// </remarks>
public static class PublishedCostLink
{
    /// <summary>Why nothing was written, in the app's own vocabulary.</summary>
    public enum LinkOutcome
    {
        /// <summary>The listing carries no SKU of the seller's, so there is nothing to join on.</summary>
        NoSku,
        /// <summary>eBay did not give back a listing ID — a reconciled publish, usually.</summary>
        NoListingId,
        /// <summary>Nothing on the deal board carries that SKU.</summary>
        NoDeal,
        /// <summary>More than one deal carries it, so which cost this is cannot be known.</summary>
        AmbiguousSku,
        /// <summary>The matching deal has no purchase price on it — there is no cost to record.</summary>
        NoPurchasePrice,
        /// <summary>That deal is already joined to a different eBay listing.</summary>
        JoinedElsewhere,
        /// <summary>A cost basis for this listing or SKU already exists, and it stands.</summary>
        AlreadyRecorded,
        /// <summary>The cost can be written.</summary>
        Link,
    }

    /// <summary>What the caller should do about the deal this publish belongs to.</summary>
    /// <param name="Outcome">Why, in one word.</param>
    /// <param name="DealId">The card to write to. 0 unless <see cref="LinkOutcome.Link"/>.</param>
    /// <param name="AdvanceToListed">Whether the card should also move to Listed.</param>
    /// <param name="Sku">The SKU the match was made on.</param>
    public sealed record CostLinkPlan(LinkOutcome Outcome, long DealId, bool AdvanceToListed, string Sku)
    {
        public bool ShouldWrite => Outcome == LinkOutcome.Link && DealId > 0;
    }

    private static readonly CostLinkPlan Nothing = new(LinkOutcome.NoSku, 0, false, "");

    /// <summary>
    /// Decides whether the listing just published can carry a cost with it.
    /// </summary>
    public static CostLinkPlan Decide(
        string? sku, string? listingId, IEnumerable<DealRecord>? deals, IEnumerable<CostBasisEntry>? costs)
    {
        var wanted = SellerSku.Sanitize(sku);
        if (wanted.Length == 0) return Nothing;

        var id = (listingId ?? "").Trim();
        if (id.Length == 0) return new CostLinkPlan(LinkOutcome.NoListingId, 0, false, wanted);

        var matches = (deals ?? [])
            .Where(d => d is not null && SkuMatches(d.Sku, wanted))
            .ToList();

        if (matches.Count == 0) return new CostLinkPlan(LinkOutcome.NoDeal, 0, false, wanted);
        if (matches.Count > 1) return new CostLinkPlan(LinkOutcome.AmbiguousSku, 0, false, wanted);

        var deal = matches[0];
        if (deal.PurchasePrice is null)
            return new CostLinkPlan(LinkOutcome.NoPurchasePrice, deal.Id, false, wanted);

        // Already pointed at some other live listing. Re-pointing it would move the cost off an item
        // that may already have sold under it.
        if (deal.ListingId.Trim().Length > 0
            && !string.Equals(deal.ListingId.Trim(), id, StringComparison.OrdinalIgnoreCase))
            return new CostLinkPlan(LinkOutcome.JoinedElsewhere, deal.Id, false, wanted);

        if (CostBasisStore.Find(costs ?? [], id, wanted) is not null)
            return new CostLinkPlan(LinkOutcome.AlreadyRecorded, deal.Id, false, wanted);

        // Publishing is what Listed means, so a card behind that moves up to it. One that is past it
        // — sold, or dropped — is left exactly where the seller put it.
        var advance = DealStages.Order(deal.Stage) < DealStages.Order(DealStages.Listed);
        return new CostLinkPlan(LinkOutcome.Link, deal.Id, advance, wanted);
    }

    /// <summary>
    /// What to tell the seller, after the write. Only the outcomes they can act on say anything —
    /// "this listing has no SKU" is true of most listings and is not news.
    /// </summary>
    /// <param name="plan">The decision that was carried out.</param>
    /// <param name="costMessage">The cost table's own sentence, when something was written.</param>
    public static string Say(CostLinkPlan plan, string? costMessage = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return plan.Outcome switch
        {
            LinkOutcome.Link =>
                $"SKU {plan.Sku} matched a deal on your board" +
                (plan.AdvanceToListed ? ", so its card moved to Listed. " : ". ") +
                (string.IsNullOrWhiteSpace(costMessage)
                    ? "What you paid for it is now recorded against this listing."
                    : costMessage.Trim()),

            LinkOutcome.AmbiguousSku =>
                $"Two deals on your board carry SKU {plan.Sku}, so the app could not tell which cost "
              + "belongs to this listing. Nothing was recorded — open the Deal Pipeline and give them "
              + "separate SKUs.",

            LinkOutcome.NoPurchasePrice =>
                $"The deal for SKU {plan.Sku} has no purchase price on it, so there was no cost to "
              + "record. Add what you paid on the Deal Pipeline and press Apply what you paid.",

            LinkOutcome.JoinedElsewhere =>
                $"The deal for SKU {plan.Sku} is already joined to a different eBay listing, so its "
              + "cost was left where it is.",

            LinkOutcome.AlreadyRecorded =>
                "This listing already has a cost basis recorded, so it was left alone.",

            _ => "",
        };
    }

    // A deal's SKU is whatever the seller typed; the one on the listing has been through the eBay
    // fence. Comparing the cleaned forms means a card labelled "WN 2026 0806" still finds the
    // listing that went out as "WN-2026-0806".
    private static bool SkuMatches(string? dealSku, string wanted)
    {
        if (string.IsNullOrWhiteSpace(dealSku)) return false;
        if (string.Equals(dealSku.Trim(), wanted, StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(SellerSku.Sanitize(dealSku), wanted, StringComparison.OrdinalIgnoreCase);
    }
}
