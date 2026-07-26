namespace ING_eBay_AutoLister.Services;

// Configurable fee assumptions for ProfitCalculator — same pattern as LiquidityScoringConfig
// (plain mutable POCO singleton, sensible hardcoded defaults). Persisted by FeeProfileStore and
// loaded into the singleton at startup, so the seller's real numbers — not zeros — are what every
// net-profit figure in the app is computed from.
//
// Replaces the EbayFeePercent/EbayFeeFixed local consts that used to be duplicated inline at two
// call sites in Program.cs's supplier-file-analyzer endpoint.
public class FeeProfile
{
    // eBay's typical final value fee — an estimate, not account-specific (varies by category and
    // store subscription level; there's no API to fetch a seller's actual negotiated rate).
    public decimal EbayFinalValueFeePercent = 13.25m;
    public decimal EbayFinalValueFeeFixed = 0.40m;

    public decimal PromotedListingRatePercent = 0m;  // opt-in — 0 means not using Promoted Listings
    public decimal PaymentProcessingPercent = 0m;     // eBay's final value fee is normally all-inclusive

    public decimal DefaultShippingCost = 0m;          // actual cost to ship (distinct from buyer-paid shipping)
    public decimal DefaultPackagingCost = 0m;
    public decimal DefaultLaborCost = 0m;

    public decimal ReturnReservePercent = 0m;
    public decimal TestingReservePercent = 0m;

    // ── The seller's own floor policy ────────────────────────────────────────────────────────
    // Break-even answers "does this lose money"; these answer "is this worth doing". They are the
    // basis for the minimum offer the app tells a seller to accept, so a negotiation that drifts
    // below the number the seller already decided on gets flagged instead of quietly accepted.
    // Both default to 0, which makes the floor exactly the break-even — the pre-existing behaviour.
    public decimal MinimumNetProfit = 0m;
    public decimal MinimumMarginPercent = 0m;

    /// <summary>
    /// Every percentage-based deduction, as a fraction of gross revenue (sale price + buyer-paid
    /// shipping). Payment processing belongs here and not in fixed costs: it scales with the sale
    /// exactly like the final value fee does.
    /// </summary>
    public decimal RevenueFeeFraction =>
        (EbayFinalValueFeePercent + PromotedListingRatePercent + PaymentProcessingPercent
         + ReturnReservePercent + TestingReservePercent) / 100m;

    /// <summary>
    /// The share of one extra dollar of sale price that actually reaches the seller. This is why
    /// buying back $10 of profit costs more than $10 of price, and it is the conversion factor
    /// behind every floor in the app.
    /// </summary>
    public decimal KeepFraction => 1m - RevenueFeeFraction;

    /// <summary>Independent copy — used to price against a change without mutating the live singleton.</summary>
    public FeeProfile Clone() => (FeeProfile)MemberwiseClone();

    /// <summary>Overwrites this profile in place, so the DI singleton every service holds updates.</summary>
    public void CopyFrom(FeeProfile other)
    {
        EbayFinalValueFeePercent = other.EbayFinalValueFeePercent;
        EbayFinalValueFeeFixed = other.EbayFinalValueFeeFixed;
        PromotedListingRatePercent = other.PromotedListingRatePercent;
        PaymentProcessingPercent = other.PaymentProcessingPercent;
        DefaultShippingCost = other.DefaultShippingCost;
        DefaultPackagingCost = other.DefaultPackagingCost;
        DefaultLaborCost = other.DefaultLaborCost;
        ReturnReservePercent = other.ReturnReservePercent;
        TestingReservePercent = other.TestingReservePercent;
        MinimumNetProfit = other.MinimumNetProfit;
        MinimumMarginPercent = other.MinimumMarginPercent;
    }

    /// <summary>
    /// Clamps a profile arriving from the browser or from disk into a range the math can survive.
    /// </summary>
    /// <remarks>
    /// Negative fees would invent profit out of nothing, and percentages summing past 100 make
    /// <see cref="KeepFraction"/> zero or negative — at which point break-even is unsolvable and
    /// every floor silently becomes infinite. Percentages are capped individually at 90 so one
    /// typo (a rate entered as 1325 rather than 13.25) cannot do it, and the minimum margin at 95
    /// so a target margin can never on its own exceed what the fees leave behind.
    /// </remarks>
    public void Sanitize()
    {
        static decimal Pct(decimal v) => Math.Clamp(v, 0m, 90m);
        static decimal Money(decimal v) => Math.Clamp(v, 0m, 1_000_000m);

        EbayFinalValueFeePercent = Pct(EbayFinalValueFeePercent);
        PromotedListingRatePercent = Pct(PromotedListingRatePercent);
        PaymentProcessingPercent = Pct(PaymentProcessingPercent);
        ReturnReservePercent = Pct(ReturnReservePercent);
        TestingReservePercent = Pct(TestingReservePercent);

        EbayFinalValueFeeFixed = Money(EbayFinalValueFeeFixed);
        DefaultShippingCost = Money(DefaultShippingCost);
        DefaultPackagingCost = Money(DefaultPackagingCost);
        DefaultLaborCost = Money(DefaultLaborCost);
        MinimumNetProfit = Money(MinimumNetProfit);
        MinimumMarginPercent = Math.Clamp(MinimumMarginPercent, 0m, 95m);

        // The individual caps above still allow five legal rates to sum past 100. Scale them back
        // proportionally rather than zeroing them, so an over-configured profile stays recognisably
        // the seller's own and still leaves a solvable 5% of every sale.
        var total = EbayFinalValueFeePercent + PromotedListingRatePercent + PaymentProcessingPercent
                  + ReturnReservePercent + TestingReservePercent;
        if (total <= 95m) return;

        var scale = 95m / total;
        EbayFinalValueFeePercent = Math.Round(EbayFinalValueFeePercent * scale, 3);
        PromotedListingRatePercent = Math.Round(PromotedListingRatePercent * scale, 3);
        PaymentProcessingPercent = Math.Round(PaymentProcessingPercent * scale, 3);
        ReturnReservePercent = Math.Round(ReturnReservePercent * scale, 3);
        TestingReservePercent = Math.Round(TestingReservePercent * scale, 3);
    }
}
