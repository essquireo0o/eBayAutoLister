namespace ING_eBay_AutoLister.Services;

// Fee assumptions for the other marketplaces the cross-listing exporter targets — same pattern as
// FeeProfile/LiquidityScoringConfig (plain mutable POCO singleton, documented defaults).
//
// These are published standard rates, not the seller's account-specific ones. None of these sites
// expose a fee API, and every one of them varies the rate by category, plan or promotion, so the
// exporter labels every number it derives from these as an estimate rather than a quote.
public class CrossListingFeeProfile
{
    // Facebook Marketplace: 5% per shipment (or a flat $0.40 minimum on shipments of $8 or less).
    // Local pickup sales are free — the exporter's note says so, since a lot of Marketplace volume
    // is local and pays no fee at all.
    public decimal FacebookFeePercent = 5.0m;
    public decimal FacebookFeeFixed = 0m;

    // Mercari: sellers pay no selling fee since the 2024 fee change — the service fee moved to the
    // buyer at checkout. Left configurable because Mercari has reversed fee policy before.
    public decimal MercariFeePercent = 0m;
    public decimal MercariFeeFixed = 0m;

    // Amazon: referral fee runs 8–15% depending on category; 15% is the common default for
    // consumer goods. Individual-plan sellers also pay $0.99 per item sold on top of this.
    public decimal AmazonFeePercent = 15.0m;
    public decimal AmazonFeeFixed = 0m;
}
