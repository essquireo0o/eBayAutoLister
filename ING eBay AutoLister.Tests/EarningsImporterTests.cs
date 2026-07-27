using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// eBay reports its fee per ORDER; earnings are tracked per line item, because each item has its own
// cost basis. Splitting that fee is the one part of the import that can misstate money without
// failing, so it is tested on its own.
public class EarningsImporterTests
{
    private static EbayOrderSummary Order(decimal? fee, params (decimal price, decimal shipping)[] lines)
    {
        var order = new EbayOrderSummary
        {
            OrderId = "ORD-1",
            CreationDate = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            PaymentStatus = "PAID",
            CancelState = "NONE_REQUESTED",
            TotalMarketplaceFee = fee,
        };

        for (var i = 0; i < lines.Length; i++)
            order.LineItems.Add(new EbayOrderLineItem
            {
                LineItemId = $"LI-{i + 1}",
                LegacyItemId = $"11000{i + 1}",
                Title = $"Item {i + 1}",
                Quantity = 1,
                LineItemCost = lines[i].price,
                ShippingCharged = lines[i].shipping,
            });

        return order;
    }

    [Fact]
    public void A_single_line_order_carries_the_whole_fee()
    {
        var flips = EarningsImporter.MapOrder(Order(132.90m, (1000m, 0m)));

        Assert.Single(flips);
        Assert.Equal(132.90m, flips[0].MarketplaceFee);
        Assert.Equal(1000m, flips[0].SalePrice);
    }

    [Fact]
    public void The_order_fee_is_split_in_proportion_to_what_each_line_brought_in()
    {
        // eBay charges on revenue, so a $900 item and a $100 item do not owe the same fee.
        var flips = EarningsImporter.MapOrder(Order(100m, (900m, 0m), (100m, 0m)));

        Assert.Equal(90m, flips[0].MarketplaceFee);
        Assert.Equal(10m, flips[1].MarketplaceFee);
    }

    [Fact]
    public void Buyer_paid_shipping_is_part_of_the_basis_the_fee_is_split_on()
    {
        var flips = EarningsImporter.MapOrder(Order(100m, (400m, 100m), (500m, 0m)));

        Assert.Equal(50m, flips[0].MarketplaceFee);
        Assert.Equal(50m, flips[1].MarketplaceFee);
    }

    [Fact]
    public void The_split_parts_add_back_up_to_ebays_own_figure_exactly()
    {
        // Three equal lines against $100 is $33.33 each, and the cent that rounding leaves over has
        // to land somewhere — otherwise the tracker drifts away from the seller's eBay statement.
        var flips = EarningsImporter.MapOrder(Order(100m, (10m, 0m), (10m, 0m), (10m, 0m)));

        Assert.Equal(100m, flips.Sum(f => f.MarketplaceFee ?? 0m));
    }

    [Fact]
    public void An_order_with_no_reported_fee_leaves_the_fee_unknown_rather_than_zero()
    {
        var flips = EarningsImporter.MapOrder(Order(null, (1000m, 0m)));

        // Null means "estimate it and say so"; zero would claim eBay sold it for free.
        Assert.Null(flips[0].MarketplaceFee);
    }

    [Fact]
    public void A_zero_value_order_splits_the_fee_evenly_instead_of_dividing_by_zero()
    {
        var flips = EarningsImporter.MapOrder(Order(10m, (0m, 0m), (0m, 0m)));

        Assert.Equal(5m, flips[0].MarketplaceFee);
        Assert.Equal(5m, flips[1].MarketplaceFee);
    }

    [Fact]
    public void The_legacy_item_id_becomes_the_listing_id_so_an_existing_cost_basis_attaches()
    {
        var flips = EarningsImporter.MapOrder(Order(10m, (100m, 0m)));

        Assert.Equal("110001", flips[0].ListingId);
    }

    [Fact]
    public void A_multi_quantity_line_carries_the_line_total_not_the_unit_price()
    {
        // eBay's lineItemCost is the extended total. Reading it as a unit price would understate
        // revenue on this sale ten-fold, and the cost of goods is scaled by quantity to match.
        var order = Order(10m, (1499.90m, 0m));
        order.LineItems[0].Quantity = 10;

        var flip = EarningsImporter.MapOrder(order)[0];

        Assert.Equal(10, flip.Quantity);
        Assert.Equal(1499.90m, flip.SalePrice);
    }

    [Fact]
    public void A_cancelled_order_maps_to_cancelled_lines()
    {
        var order = Order(100m, (1000m, 0m));
        order.CancelState = "CANCELED";

        Assert.Equal("cancelled", EarningsImporter.MapOrder(order)[0].Status);
    }

    [Fact]
    public void A_refunded_line_carries_its_refund_and_is_marked_refunded()
    {
        var order = Order(100m, (1000m, 0m));
        order.LineItems[0].RefundedAmount = 250m;

        var flip = EarningsImporter.MapOrder(order)[0];

        Assert.Equal("refunded", flip.Status);
        Assert.Equal(250m, flip.RefundedAmount);
    }

    [Fact]
    public void A_refund_larger_than_the_sale_is_clamped_rather_than_producing_negative_revenue()
    {
        var order = Order(100m, (1000m, 0m));
        order.LineItems[0].RefundedAmount = 5000m;

        Assert.Equal(1000m, EarningsImporter.MapOrder(order)[0].RefundedAmount);
    }

    [Fact]
    public void An_untitled_line_gets_a_placeholder_rather_than_failing_validation()
    {
        var order = Order(10m, (100m, 0m));
        order.LineItems[0].Title = "";

        Assert.Equal("(untitled eBay item)", EarningsImporter.MapOrder(order)[0].Title);
    }

    [Fact]
    public void An_unpaid_order_is_not_countable()
    {
        var order = Order(0m, (1000m, 0m));
        order.PaymentStatus = "PENDING";

        // A promise to pay is not money made. Counting it would put profit in the headline that can
        // still evaporate, and then take it back out on the next import.
        Assert.False(EarningsImporter.IsCountable(order));
    }

    [Fact]
    public void Paid_and_refunded_orders_are_both_countable()
    {
        var paid = Order(0m, (1000m, 0m));
        var refunded = Order(0m, (1000m, 0m));
        refunded.PaymentStatus = "PARTIALLY_REFUNDED";

        Assert.True(EarningsImporter.IsCountable(paid));
        Assert.True(EarningsImporter.IsCountable(refunded));
    }

    [Fact]
    public void An_order_with_no_line_items_produces_nothing()
    {
        var order = Order(100m);

        Assert.Empty(EarningsImporter.MapOrder(order));
        Assert.False(EarningsImporter.IsCountable(order));
    }
}
