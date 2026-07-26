using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

// Centralizes the NetProfit/ROI/Margin/break-even math the spec calls for, using a configurable
// FeeProfile instead of hardcoded assumptions. Replaces the two near-identical inline fee blocks
// that used to live in Program.cs's supplier-file-analyzer endpoint (local-data path and
// Terapeak-fallback path each hand-rolled the same formula).
public sealed class ProfitCalculator
{
    public ProfitBreakdown Calculate(
        decimal supplierUnitCost, int quantity, decimal expectedSalePrice, decimal quickSalePrice,
        decimal buyerPaidShipping, FeeProfile fees,
        decimal? actualShippingCostOverride = null, decimal? otherCosts = null)
    {
        quantity = Math.Max(1, quantity);
        var actualShipping = actualShippingCostOverride ?? fees.DefaultShippingCost;
        var other = otherCosts ?? 0m;

        var totalRevenue = expectedSalePrice + buyerPaidShipping;
        var ebayFees = Math.Round(totalRevenue * (fees.EbayFinalValueFeePercent / 100m) + fees.EbayFinalValueFeeFixed, 2);
        var promotedFees = Math.Round(totalRevenue * (fees.PromotedListingRatePercent / 100m), 2);
        // Payment processing is reported on its own line rather than folded into "other costs":
        // sellers outside eBay's all-inclusive final value fee (and every cross-listed marketplace)
        // pay it separately, and a fee the seller can't see is a fee they can't price around.
        var paymentFees = Math.Round(totalRevenue * (fees.PaymentProcessingPercent / 100m), 2);
        var returnReserve = Math.Round(totalRevenue * (fees.ReturnReservePercent / 100m), 2);
        var testingReserve = Math.Round(totalRevenue * (fees.TestingReservePercent / 100m), 2);

        var netProfitPerUnit = totalRevenue - supplierUnitCost - ebayFees - promotedFees - paymentFees
            - actualShipping - fees.DefaultPackagingCost - fees.DefaultLaborCost
            - returnReserve - testingReserve - other;

        var breakEven = BreakEvenPrice(
            supplierUnitCost, actualShipping, fees.DefaultPackagingCost, fees.DefaultLaborCost, other,
            buyerPaidShipping, fees.EbayFinalValueFeeFixed, fees.RevenueFeeFraction);

        return new ProfitBreakdown
        {
            SupplierUnitCost = supplierUnitCost,
            Quantity = quantity,
            ExpectedSalePrice = expectedSalePrice,
            QuickSalePrice = quickSalePrice,
            BuyerPaidShipping = buyerPaidShipping,
            EbayFees = ebayFees,
            PromotedListingFees = promotedFees,
            ActualShippingCost = actualShipping,
            PackagingCost = fees.DefaultPackagingCost,
            LaborCost = fees.DefaultLaborCost,
            ReturnReserve = returnReserve,
            TestingReserve = testingReserve,
            PaymentProcessingFees = paymentFees,
            OtherCosts = other,
            NetProfitPerUnit = Math.Round(netProfitPerUnit, 2),
            TotalPotentialProfit = Math.Round(netProfitPerUnit * quantity, 2),
            RoiPercent = supplierUnitCost > 0 ? Math.Round(netProfitPerUnit / supplierUnitCost * 100m, 1) : null,
            MarginPercent = totalRevenue > 0 ? Math.Round(netProfitPerUnit / totalRevenue * 100m, 1) : null,
            BreakEvenSalePrice = Math.Round(breakEven, 2),
        };
    }

    // Solves for the sale price P where NetProfit(P) == 0, given that every percentage-based fee
    // is itself a function of P (via TotalRevenue = P + shipping) — algebraic rearrangement of the
    // NetProfit formula, not a numeric search:
    //   P*(1 - totalFeePct) = fixedCosts - shipping*(1 - totalFeePct)
    //   P = fixedCosts / (1 - totalFeePct) - shipping
    //
    // totalFeeFraction comes from FeeProfile.RevenueFeeFraction so payment processing is scaled
    // with the sale here too. It used to be passed in as a fixed cost, which understated the
    // break-even for any seller who configured it — harmless while the rate defaulted to 0 and
    // nothing could set it, wrong the moment the Fees & Costs screen could.
    private static decimal BreakEvenPrice(
        decimal supplierUnitCost, decimal actualShipping, decimal packaging, decimal labor, decimal other,
        decimal buyerPaidShipping, decimal feeFixed, decimal totalFeeFraction)
    {
        if (totalFeeFraction >= 1m) return decimal.MaxValue; // fees alone exceed revenue — no price breaks even

        var fixedCosts = supplierUnitCost + feeFixed + actualShipping + packaging + labor + other;
        return fixedCosts / (1m - totalFeeFraction) - buyerPaidShipping;
    }
}
