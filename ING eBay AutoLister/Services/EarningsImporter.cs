using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Turns eBay orders into flip records. Pure — no I/O — so the one part of the import that can
/// silently misstate money (splitting the order's fee across its lines) is testable in isolation.
/// </summary>
public static class EarningsImporter
{
    /// <summary>Maps one order to one flip per line item.</summary>
    /// <remarks>
    /// eBay reports <c>totalMarketplaceFee</c> per ORDER, not per line, but earnings are tracked per
    /// line because each item has its own cost basis. The fee is therefore allocated across the
    /// lines in proportion to what each line brought in — the same basis eBay charges on — rather
    /// than split evenly, which would overstate the fee on a cheap item bought alongside an
    /// expensive one and understate it on the expensive one.
    /// </remarks>
    public static List<FlipRecord> MapOrder(EbayOrderSummary order)
    {
        var flips = new List<FlipRecord>();
        if (order.LineItems.Count == 0) return flips;

        var lineTotals = order.LineItems
            .Select(l => Math.Max(0m, l.LineItemCost + l.ShippingCharged))
            .ToList();
        var orderTotal = lineTotals.Sum();

        var cancelled = !string.IsNullOrWhiteSpace(order.CancelState)
                        && !order.CancelState.Equals("NONE_REQUESTED", StringComparison.OrdinalIgnoreCase);

        // Allocated with a running remainder so the parts add back up to eBay's figure exactly. The
        // obvious per-line rounding leaves cents unaccounted for, and cents that belong to nobody
        // are how a total drifts away from the seller's eBay statement.
        var allocated = 0m;

        for (var i = 0; i < order.LineItems.Count; i++)
        {
            var line = order.LineItems[i];
            decimal? fee = null;

            if (order.TotalMarketplaceFee.HasValue)
            {
                var total = order.TotalMarketplaceFee.Value;
                if (i == order.LineItems.Count - 1)
                {
                    fee = Math.Round(total - allocated, 2);
                }
                else
                {
                    var share = orderTotal > 0 ? lineTotals[i] / orderTotal : 1m / order.LineItems.Count;
                    fee = Math.Round(total * share, 2);
                    allocated += fee.Value;
                }
            }

            var refunded = Math.Clamp(line.RefundedAmount, 0m, line.LineItemCost + line.ShippingCharged);
            var status = cancelled ? "cancelled"
                : refunded > 0 ? "refunded"
                : "paid";

            flips.Add(new FlipRecord
            {
                Source = "ebay",
                OrderId = order.OrderId,
                LineItemId = line.LineItemId,
                // legacyItemId is the classic numeric listing ID — the same key CostBasisStore,
                // Inventory Health and the listing cards use, so a cost entered anywhere in the app
                // attaches to this sale without the seller doing anything.
                ListingId = line.LegacyItemId,
                Sku = line.Sku,
                Title = string.IsNullOrWhiteSpace(line.Title) ? "(untitled eBay item)" : line.Title,
                SoldUtc = order.CreationDate,
                Quantity = Math.Max(1, line.Quantity),
                // lineItemCost is the EXTENDED line total, not a unit price — verified against real
                // orders on the connected account, where the totals divide into clean unit prices
                // ($1,499.90 over 10 units is $149.99 each). Reading it as per-unit would understate
                // revenue on every multi-quantity sale by the quantity. Cost of goods is scaled by
                // quantity to match (EarningsCalculator), so both sides are line totals.
                SalePrice = Math.Round(line.LineItemCost, 2),
                ShippingCharged = Math.Round(line.ShippingCharged, 2),
                MarketplaceFee = fee,
                RefundedAmount = Math.Round(refunded, 2),
                Status = status,
            });
        }

        return flips;
    }

    /// <summary>
    /// Whether an order is far enough along to be counted as money made.
    /// </summary>
    /// <remarks>
    /// An unpaid order is a promise, not a sale. Counting one would put profit in the headline that
    /// can still evaporate, and then take it back out again on the next import — which reads as the
    /// tracker being wrong rather than as the buyer never paying.
    /// </remarks>
    public static bool IsCountable(EbayOrderSummary order) =>
        order.LineItems.Count > 0
        && (string.IsNullOrWhiteSpace(order.PaymentStatus)
            || order.PaymentStatus.Equals("PAID", StringComparison.OrdinalIgnoreCase)
            || order.PaymentStatus.Equals("FULLY_REFUNDED", StringComparison.OrdinalIgnoreCase)
            || order.PaymentStatus.Equals("PARTIALLY_REFUNDED", StringComparison.OrdinalIgnoreCase));
}
