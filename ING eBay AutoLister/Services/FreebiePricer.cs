using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// What a free thing actually costs. Pure, and small on purpose: the profit maths itself is the
/// shared <see cref="ProfitCalculator"/> — this only decides the cost basis to hand it, and the
/// sentence explaining where that basis came from.
/// </summary>
/// <remarks>
/// <para>
/// The temptation on this board is to pass zero and be done. Three things say otherwise, and each
/// of them is the difference between a flip that works and one that doesn't:
/// </para>
/// <list type="number">
///   <item>
///     <b>Sales tax survives a rebate.</b> A $49.99 item with a $49.99 mail-in rebate is not free.
///     The register charged tax on the full $49.99 and the rebate cheque only ever covers the price
///     — so at 7.5% the seller is permanently $3.75 down on an item this board would otherwise show
///     at $0. On a flip netting $20 that is a sixth of the margin.
///   </item>
///   <item>
///     <b>A rebate is a claim, not a discount.</b> The money leaves now and comes back later, if it
///     comes back — see <see cref="RebateReservePercent"/>.
///   </item>
///   <item>
///     <b>The wait is real.</b> Cash tied up in a rebate is cash that can't buy the next flip, so
///     the wait is added to the days-to-cash figure the whole board is ranked by.
///   </item>
/// </list>
/// </remarks>
public static class FreebiePricer
{
    /// <summary>
    /// The slice of a rebate deliberately not counted as money.
    /// </summary>
    /// <remarks>
    /// This is a <b>reserve, not a forecast</b>. Nobody here knows what share of mail-in rebates go
    /// unpaid, and inventing a denial rate and calling it data would be worse than useless. What
    /// this says is a policy: a row only earns its verdict if it still works when roughly one claim
    /// in seven is never paid. Erring toward a higher cost understates profit, which is the safe
    /// direction for a number somebody spends money on.
    /// </remarks>
    public const decimal RebateReservePercent = 15m;

    /// <summary>
    /// How long a mail-in rebate takes to arrive, in days. Rebate forms routinely quote eight to
    /// twelve weeks; this takes the short end, because the point is to stop a rebate flip being
    /// ranked as if the cash came back as fast as a curb pickup's, not to model a mailroom.
    /// </summary>
    public const int MailInRebateWaitDays = 56;

    /// <summary>
    /// Rebates paid through an app rather than by post — Venmo, PayPal, Ibotta and the rest. Days,
    /// not months, and named on the listing when it is one of them.
    /// </summary>
    public const int AppRebateWaitDays = 14;

    /// <summary>
    /// The cost basis and everything that explains it. <paramref name="salesTaxPercent"/> is the
    /// seller's own rate and applies only where a till is involved: a curb pickup off a stranger
    /// charges none, whatever the form says.
    /// </summary>
    public static FreebieEconomics Cost(FreebieDetails details, decimal salesTaxPercent)
    {
        // Private-party pickup is cash between two people. Only a retailer rings up tax.
        var taxPercent = details.IsPickup ? 0m : RetailBuyCosts.Sanitize(salesTaxPercent);

        var economics = new FreebieEconomics
        {
            Kind = details.Kind,
            KindLabel = details.KindLabel,
            Urgency = details.Urgency,
            ExpiresUtc = details.ExpiresUtc,
            ExpiresText = details.ExpiresText,
            RequiresCoupon = details.RequiresCoupon,
            IsPickup = details.IsPickup,
            IsBulky = details.IsBulky,
        };

        switch (details.Kind)
        {
            case FreebieKinds.FreeAfterRebate:
            {
                // Charged on the sticker, because that is what the register rings up — the rebate is
                // settled weeks later and by somebody else entirely.
                var tax = RetailBuyCosts.TaxOn(details.ListPrice, taxPercent);
                var reserve = Math.Round(details.RebateAmount * RebateReservePercent / 100m, 2);

                economics.OutOfPocketNow = Math.Round(details.ListPrice + tax, 2);
                economics.RefundExpected = details.RebateAmount;
                economics.SalesTax = tax;
                economics.RebateReserve = reserve;
                economics.NetCost = Math.Round(economics.OutOfPocketNow - details.RebateAmount + reserve, 2);
                economics.RefundWaitDays = details.RebateVia.Length > 0 ? AppRebateWaitDays : MailInRebateWaitDays;

                var shortfall = Math.Round(details.ListPrice - details.RebateAmount, 2);
                economics.CostNote =
                    $"You pay {Money(economics.OutOfPocketNow)} today and claim {Money(details.RebateAmount)} back" +
                    $"{(details.RebateVia.Length > 0 ? $" via {details.RebateVia}" : " by post")}. " +
                    $"Sales tax is never refunded, so {Money(tax)} of it is gone for good" +
                    $"{(shortfall > 0 ? $", and {Money(shortfall)} of the price isn't covered either" : "")}. " +
                    $"{Money(reserve)} more is held back in case the claim isn't paid.";
                break;
            }

            case FreebieKinds.FreeAfterCoupon:
                // The code takes the price to zero, and a till charges tax on what it actually
                // rings up — which is nothing. What this row costs is the risk the code has died,
                // and that is said on the row rather than priced as a number nobody can check.
                economics.NetCost = 0m;
                economics.CostNote =
                    "Free at checkout, but only with the code — without it this is a full-price item, " +
                    "so check the price before you commit to it.";
                break;

            case FreebieKinds.NearFree:
            {
                var tax = RetailBuyCosts.TaxOn(details.ListPrice, taxPercent);
                economics.OutOfPocketNow = Math.Round(details.ListPrice + tax, 2);
                economics.SalesTax = tax;
                economics.NetCost = economics.OutOfPocketNow;
                economics.CostNote = tax > 0
                    ? $"{Money(details.ListPrice)} plus {Money(tax)} tax — near enough to nothing that almost all of the resale is profit."
                    : $"{Money(details.ListPrice)} — near enough to nothing that almost all of the resale is profit.";
                break;
            }

            default:
                economics.NetCost = 0m;
                economics.CostNote = details.IsPickup
                    ? "Nothing but the drive. Every dollar it sells for, minus eBay's cut and the shipping, is profit."
                    : "Nothing. Every dollar it sells for, minus eBay's cut and the shipping, is profit.";
                break;
        }

        economics.CappedReason = CapReason(details);
        economics.UrgencyNote = UrgencyNote(details);
        return economics;
    }

    /// <summary>
    /// Why this row's verdict is held below what its arithmetic earned, or null when nothing is
    /// being held back.
    /// </summary>
    /// <remarks>
    /// A $0 cost basis makes ROI unbounded, so <b>every unflagged freebie clears the goldmine bar on
    /// return alone</b> and the badge stops carrying information. These two cases are the ones where
    /// the missing cost is large enough to change the answer, and both are said out loud rather than
    /// quietly folded into a number.
    /// </remarks>
    public static string? CapReason(FreebieDetails details)
    {
        // On a $0 item an unknown delivery charge isn't a rounding error — it is the entire cost
        // basis. A "free" thing with $18 shipping is an $18 item, and the listing didn't say.
        if (!details.DeliveryCostKnown && !details.IsPickup)
            return "The listing doesn't say what delivery costs, and on a free item the shipping IS the price.";

        // A sofa's sold comps were set by sellers who arranged freight or sold it locally. The
        // parcel-shaped shipping cost in the profit maths below is not what moving this costs.
        if (details.IsBulky)
            return "Big and heavy — the eBay comps behind this price assume it fits in a box, and this doesn't. " +
                   "Worth having if you can move it, but sell it locally rather than shipping it.";

        return null;
    }

    /// <summary>
    /// The deadline, said plainly. A profit figure read after the offer ended is not a profit
    /// figure, and on this board that is the normal way to lose the deal rather than the rare one.
    /// </summary>
    public static string UrgencyNote(FreebieDetails details) => details.Urgency switch
    {
        FreebieUrgency.Today => details.ExpiresText.Length > 0
            ? $"Gone today — the listing says {details.ExpiresText}."
            : "Gone today — this one is stated to end within the day.",

        FreebieUrgency.ThisWeek => details.ExpiresText.Length > 0
            ? $"Ends this week ({details.ExpiresText})."
            : "Ends this week.",

        FreebieUrgency.FirstCome =>
            "First come, first served — free posts are usually claimed the same day, so message now " +
            "and collect today or assume it's gone.",

        _ => details.Kind == FreebieKinds.Free || details.Kind == FreebieKinds.FreeAfterCoupon
            ? "No stated deadline, but free offers are pulled without notice — check the price before you commit."
            : "No stated deadline.",
    };

    /// <summary>
    /// True when the profit is too small to be worth fetching, photographing, listing and packing —
    /// which on a free item is the only cost there is. Free does not make a $6 flip worth a Saturday.
    /// </summary>
    public static bool NotWorthCollecting(decimal? netProfit) =>
        netProfit is > 0m and < FreebieSelectors.MinWorthCollecting;

    private static string Money(decimal value) => $"${value:0.##}";
}
