using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The best legal combination of coupons at one price, and what it leaves the buy costing. Pure,
/// and small on purpose: the profit maths is still the shared <see cref="ProfitCalculator"/> — this
/// only decides the cost basis to hand it, exactly as <see cref="FreebiePricer"/> does for a free
/// item.
/// </summary>
/// <remarks>
/// <para>
/// Almost all of this file is rules about what NOT to count, because "stacking" is the part of
/// couponing that people get wrong with real money. Four rules do the work:
/// </para>
/// <list type="number">
///   <item>
///     <b>One code per order.</b> Essentially every checkout on earth takes a single promo code, so
///     two 20% codes are not 40%. The best single code is applied and the rest are shown as
///     alternatives — and if the deal's own price already needs a code, nothing stacks on it at all.
///   </item>
///   <item>
///     <b>A discount with no code is already in the price.</b> A sitewide sale needing no code is
///     what the shelf price on the deal feed already reflects; subtracting it again would discount
///     the item twice and fabricate a margin.
///   </item>
///   <item>
///     <b>Cashback is a rebate, not a discount.</b> It is paid by a portal, weeks later, on what was
///     actually spent — so it stacks with a code, but part of it is held in reserve and the wait is
///     stated rather than hidden. Same posture as <see cref="FreebiePricer.RebateReservePercent"/>.
///   </item>
///   <item>
///     <b>Tax follows the discount.</b> A retailer's own code lowers what the register rings up, so
///     the code saves its face value plus the tax that would have sat on top of it. That is real
///     money and it is the one place this file is allowed to be generous.
///   </item>
/// </list>
/// </remarks>
public static class CouponStacker
{
    /// <summary>
    /// The slice of a cashback claim deliberately not counted as money. Shares
    /// <see cref="FreebiePricer.RebateReservePercent"/> rather than inventing a second number: a
    /// portal claim that goes untracked and a rebate cheque that never arrives are the same risk,
    /// and two different reserves would be two different opinions about it.
    /// </summary>
    public const decimal CashbackReservePercent = FreebiePricer.RebateReservePercent;

    /// <summary>
    /// How long cashback takes to arrive, in days. Portals hold a claim until the retailer's return
    /// window has closed and then pay on a cycle; two months is the short end of what that means in
    /// practice. The point is to stop cashback being counted as if it landed at the till.
    /// </summary>
    public const int CashbackWaitDays = 60;

    /// <summary>
    /// The best stack at this price.
    /// </summary>
    /// <param name="subtotal">The shelf price, before tax and before anything comes off it.</param>
    /// <param name="existingCode">
    /// The code the advertised price already requires, if any. Its presence is what blocks a second
    /// code: the seller cannot type two.
    /// </param>
    /// <param name="shipsFree">
    /// Whether the deal already ships free, which decides whether a free-shipping code is worth
    /// surfacing at all. Never worth money here either way — see <see cref="CouponKinds.FreeShipping"/>.
    /// </param>
    public static CouponStack Best(
        IEnumerable<CouponOffer>? offers, decimal subtotal, decimal salesTaxPercent,
        string existingCode = "", bool shipsFree = false, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var taxPercent = RetailBuyCosts.Sanitize(salesTaxPercent);
        // Expired, and — the one the live feeds taught — bound to somebody else's item. A store's
        // coupon list is mostly codes posted against one specific deal ("$40 off when you apply
        // promo code LUSF2737 = $189.99"); against any other item at that store they buy nothing,
        // so they are not even offered here as alternatives. The standalone store lookup still
        // lists them, where the item they belong to is visible.
        var all = (offers ?? [])
            .Where(o => !Expired(o, now))
            .Where(o => o.AppliesToOrder || o.Kind == CouponKinds.Cashback)
            .ToList();

        var stack = new CouponStack
        {
            Subtotal = subtotal,
            DiscountedSubtotal = subtotal,
            SalesTax = RetailBuyCosts.TaxOn(subtotal, taxPercent),
            NetCost = RetailBuyCosts.AllInCost(subtotal, taxPercent),
            Confidence = CouponConfidence.Low,
        };

        if (subtotal <= 0 || all.Count == 0)
        {
            stack.Note = all.Count == 0 ? "No public codes found for this store." : "Nothing to discount.";
            return stack;
        }

        // The one code the seller can actually type, and the only discount counted. Ties go to the
        // one worth more; equal money goes to the one worth believing.
        var codeOffer = existingCode.Length > 0
            ? null
            : all.Where(o => BankableDiscount(o, subtotal))
                 .OrderByDescending(o => o.DiscountOn(subtotal))
                 .ThenByDescending(o => CouponConfidence.Rank(o.Confidence))
                 .ThenByDescending(o => o.ExpiresUtc ?? DateTime.MaxValue)
                 .FirstOrDefault();

        // Paid by somebody who isn't the retailer, so it survives the one-code rule — including on a
        // deal whose price already needs a code.
        var cashbackOffer = all
            .Where(BankableCashback)
            .OrderByDescending(o => o.Value)
            .ThenByDescending(o => CouponConfidence.Rank(o.Confidence))
            .FirstOrDefault();

        var notes = new List<string>();

        if (codeOffer is not null)
        {
            stack.Applied.Add(codeOffer);
            stack.Discount = codeOffer.DiscountOn(subtotal);
            stack.DiscountedSubtotal = Math.Round(subtotal - stack.Discount, 2);
            stack.SalesTax = RetailBuyCosts.TaxOn(stack.DiscountedSubtotal, taxPercent);

            var taxSaved = Math.Round(RetailBuyCosts.TaxOn(subtotal, taxPercent) - stack.SalesTax, 2);
            notes.Add($"Code {codeOffer.Code} takes {Money(subtotal)} down to {Money(stack.DiscountedSubtotal)}" +
                      (taxSaved > 0 ? $", and {Money(taxSaved)} of sales tax comes off with it." : "."));
        }
        else if (existingCode.Length > 0 && all.Any(o => BankableDiscount(o, subtotal)))
        {
            notes.Add($"This price already needs code {existingCode}, and a checkout takes one code — " +
                      "so the store-wide codes below can't stack on top of it. Try whichever is worth more.");
        }

        var netCostBeforeCashback = Math.Round(stack.DiscountedSubtotal + stack.SalesTax, 2);
        stack.NetCost = netCostBeforeCashback;

        if (cashbackOffer is not null)
        {
            stack.Applied.Add(cashbackOffer);
            // Paid on what was actually spent, which is the discounted price — a portal does not pay
            // a percentage of a price nobody was charged.
            stack.CashbackExpected = Math.Round(stack.DiscountedSubtotal * cashbackOffer.Value / 100m, 2);
            stack.CashbackReserve = Math.Round(stack.CashbackExpected * CashbackReservePercent / 100m, 2);
            stack.CashbackWaitDays = CashbackWaitDays;
            stack.NetCost = Math.Round(netCostBeforeCashback - stack.CashbackExpected + stack.CashbackReserve, 2);

            notes.Add($"{cashbackOffer.MerchantLabel} pays {cashbackOffer.Value:0.##}% back " +
                      $"({Money(stack.CashbackExpected)}) about {CashbackWaitDays} days after the order — " +
                      $"{Money(stack.CashbackReserve)} of that is held back here in case the claim never tracks, " +
                      "and none of it is money you have while the item is still on the shelf.");
        }

        // Everything the seller might still want, in the order it is worth reading: the codes that
        // could have applied, then the ones gated behind a bigger order, then free shipping.
        stack.AlsoFound = all
            .Where(o => !stack.Applied.Contains(o))
            .Where(o => o.Kind != CouponKinds.FreeShipping || !shipsFree)
            .OrderByDescending(o => o.DiscountOn(subtotal))
            .ThenBy(o => o.MinSpend)
            .Take(MaxAlternativesShown)
            .ToList();

        if (stack.AlsoFound.Any(o => o.Kind == CouponKinds.FreeShipping))
        {
            notes.Add("A free-shipping code was found too. What it saves depends on what they were going to " +
                      "charge to ship it, which isn't published — so it isn't counted in the figures.");
        }

        stack.Confidence = stack.Applied.Count == 0
            ? CouponConfidence.Low
            : stack.Applied.Select(o => o.Confidence).Aggregate(CouponConfidence.High, CouponConfidence.Min);

        if (stack.Applied.Count == 0 && notes.Count == 0)
        {
            notes.Add(all.Count == 1
                ? "One offer found for this store, but nothing that can be counted against this price."
                : $"{all.Count} offers found for this store, but nothing that can be counted against this price.");
        }

        // The caveat goes last, so it reads as the qualification on the money rather than as the
        // headline — but it is never omitted on a stack the app is not confident in.
        if (stack.HasSaving && stack.Confidence == CouponConfidence.Low)
        {
            var why = stack.Applied.Select(o => o.ConfidenceNote).FirstOrDefault(n => n.Length > 0);
            notes.Add($"Treat this as a lead rather than a price. {why}".TrimEnd());
        }

        stack.Note = string.Join(" ", notes);
        return stack;
    }

    /// <summary>How many alternatives are worth showing beside a row before the list becomes noise.</summary>
    public const int MaxAlternativesShown = 6;

    /// <summary>
    /// Whether a discount offer can be counted against this price.
    /// </summary>
    /// <remarks>
    /// The share test on an ungated amount-off code is the one that earns its place: "$100 off"
    /// beside a $120 item is, essentially always, "$100 off $1,000" with the threshold written
    /// somewhere the parser couldn't reach. Counting it would report an 83% discount and put a
    /// fabricated goldmine at the top of the board.
    /// </remarks>
    public static bool BankableDiscount(CouponOffer offer, decimal subtotal)
    {
        if (offer.Kind is not (CouponKinds.PercentOff or CouponKinds.AmountOff)) return false;
        if (offer.Value <= 0 || subtotal <= 0) return false;

        // A discount that needs no code is a sale, and a sale is already in the price on the page.
        if (offer.Code.Length == 0) return false;

        // Posted against one specific listing. Typing it at a different item's checkout buys
        // nothing — see CouponOffer.AppliesToOrder.
        if (!offer.AppliesToOrder) return false;

        // Gated behind an order this item doesn't reach. Not a failure — it is shown as an
        // alternative, because a seller buying two of them does reach it.
        if (offer.MinSpend > subtotal) return false;

        if (offer.Kind == CouponKinds.PercentOff && offer.Value > CouponSelectors.MaxCrediblePercentOff) return false;

        if (offer.Kind == CouponKinds.AmountOff && offer.MinSpend <= 0
            && offer.DiscountOn(subtotal) > subtotal * CouponSelectors.MaxUngatedDiscountShare)
            return false;

        return true;
    }

    public static bool BankableCashback(CouponOffer offer) =>
        offer.Kind == CouponKinds.Cashback
        && offer.Value >= CouponSelectors.MinCredibleCashbackPercent
        && offer.Value <= CouponSelectors.MaxCredibleCashbackPercent;

    /// <summary>
    /// Past its stated deadline. Offers with no stated deadline are not expired — they are merely
    /// unconfirmed, which is what their confidence grade already says.
    /// </summary>
    public static bool Expired(CouponOffer offer, DateTime nowUtc) =>
        offer.ExpiresUtc is { } expires && expires < nowUtc;

    private static string Money(decimal value) => $"${value:0.##}";
}
