using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The costs that differ by what the thing IS, applied by turning them into arguments for the same
/// <see cref="ProfitCalculator"/> every other screen uses.
/// </summary>
/// <remarks>
/// <para>
/// The parcel assumptions are wrong for a car in three separate ways, and all three move real
/// money. eBay charges a <b>flat</b> successful-listing fee on a vehicle rather than the percentage
/// final value fee — on an $8,000 truck the difference between the two is over a thousand dollars,
/// in the direction that invents a loss. There is <b>no shipping</b>, because the buyer drives it
/// away, so a shipping cost and a packaging cost are both fiction. And there is a <b>title</b> to
/// transfer, which is a real bill the parcel model has no line for.
/// </para>
/// <para>
/// Rather than teach <see cref="ProfitCalculator"/> about vehicles, this maps a category onto the
/// arguments it already takes: a cloned <see cref="FeeProfile"/> with the percentage rates zeroed
/// and the flat fee in the fixed slot, zero shipping on both sides, and the title and transport as
/// <c>otherCosts</c>. One fee engine, one break-even solver, one set of rounding rules — the
/// category only decides what it is handed.
/// </para>
/// <para>
/// The dollar figures below are defaults of exactly the same kind as the 13.25% final value fee
/// already in <see cref="FeeProfile"/>: right in spirit for most sellers, wrong by some margin for
/// any particular one, and stated on the row so they can be argued with. They are not on
/// <see cref="FeeProfile"/> for the same reason sales tax isn't — they apply to a handful of
/// categories, and putting them there would start charging a title transfer on a games console.
/// </para>
/// </remarks>
public static class CategoryCosts
{
    /// <summary>
    /// eBay Motors' flat successful-listing fee for a car, truck or RV. Flat, not a percentage —
    /// which is the whole reason vehicles need their own cost model.
    /// </summary>
    public const decimal VehicleSaleFee = 125m;

    /// <summary>The same fee for the smaller titled things: motorcycles, quads, skis, boats, trailers.</summary>
    public const decimal PowersportsSaleFee = 60m;

    /// <summary>
    /// Title and registration transfer on a titled buy. Varies by state and by whether the seller
    /// registers it at all; charged rather than ignored, because understating a cost overstates a
    /// profit somebody is about to spend money against.
    /// </summary>
    public const decimal TitleTransferCost = 85m;

    /// <summary>
    /// Getting it home. Zero by default and deliberately so: on almost every local vehicle flip the
    /// buyer drives it away, and inventing a tow bill for the ones that don't would be a made-up
    /// number in the middle of an otherwise checkable sum. The row says it wasn't costed.
    /// </summary>
    public const decimal DefaultTransportCost = 0m;

    /// <summary>Everything <see cref="LocalArbitrageAnalyzer"/> needs to price one row in one category.</summary>
    /// <param name="Fees">The profile to hand <see cref="ProfitCalculator"/> — the seller's own, or a category-adjusted clone of it.</param>
    /// <param name="BuyerPaidShipping">Shipping revenue. Zero whenever the buyer collects, because nobody pays postage on a sofa.</param>
    /// <param name="ShippingOverride">What it actually costs to ship. Zero (not null) on a collected item, so the profile's default shipping cost can't leak back in.</param>
    /// <param name="OtherCosts">Title plus transport. Deducted from profit, and reported apart from the marketplace fees.</param>
    /// <param name="Economics">The same answer in words, for the row.</param>
    public sealed record CategoryQuote(
        FeeProfile Fees, decimal BuyerPaidShipping, decimal? ShippingOverride, decimal OtherCosts,
        CategoryEconomics Economics);

    /// <summary>
    /// Costs one row's category.
    /// </summary>
    /// <param name="avgCompShipping">
    /// What buyers paid for shipping on the matched comps. Booked as revenue and as cost on a parcel
    /// row; ignored entirely when the buyer collects.
    /// </param>
    public static CategoryQuote For(ResaleCategory category, FeeProfile baseFees, decimal avgCompShipping)
    {
        var economics = new CategoryEconomics
        {
            CategoryId = category.Id,
            CategoryLabel = category.Label,
        };

        switch (category.Channel)
        {
            case SaleChannel.EbayMotors:
            {
                var fees = baseFees.Clone();
                // The percentage fees are the parcel model. A vehicle sale is charged a flat amount,
                // so every rate that scales with the sale price goes to zero and the flat fee takes
                // the fixed slot the $0.40 listing fee normally occupies.
                fees.EbayFinalValueFeePercent = 0m;
                fees.PromotedListingRatePercent = 0m;
                fees.PaymentProcessingPercent = 0m;
                fees.ReturnReservePercent = 0m;
                fees.TestingReservePercent = 0m;
                fees.EbayFinalValueFeeFixed = category.FlatSaleFee;
                // Nothing is boxed, labelled or carried to a depot.
                fees.DefaultShippingCost = 0m;
                fees.DefaultPackagingCost = 0m;
                fees.DefaultLaborCost = 0m;

                economics.ChannelLabel = "eBay Motors · buyer collects";
                economics.FeeBasis = $"{category.FlatSaleFee:C0} flat vehicle fee";
                economics.ShipsToBuyer = false;
                economics.TitleCost = category.IsTitled ? TitleTransferCost : 0m;
                economics.TransportCost = DefaultTransportCost;
                economics.Note =
                    $"Costed as a vehicle: {category.FlatSaleFee:C0} flat eBay Motors fee instead of the percentage " +
                    $"final value fee, no shipping because the buyer collects, and {TitleTransferCost:C0} for the title " +
                    "transfer. Transport isn't costed — if this one has to be towed, take that off the profit yourself.";

                return new CategoryQuote(fees, 0m, 0m, economics.ExtraCostTotal, economics);
            }

            case SaleChannel.EbayLocalPickup:
            {
                var fees = baseFees.Clone();
                // eBay's cut still applies — this sells on eBay — but a local-pickup listing has no
                // label and no box, and charging the profile's parcel shipping against a fridge is
                // how a real flip reads as a loss.
                fees.DefaultShippingCost = 0m;
                fees.DefaultPackagingCost = 0m;

                economics.ChannelLabel = "eBay · buyer collects";
                economics.FeeBasis = FeeBasisText(fees);
                economics.ShipsToBuyer = false;
                economics.Note =
                    "Costed as a local-pickup sale: eBay's percentage fee still applies, but nothing is boxed or " +
                    "posted, so no shipping or packaging is charged against the profit.";

                return new CategoryQuote(fees, 0m, 0m, 0m, economics);
            }

            default:
            {
                // The parcel model, untouched: the seller's own profile, comp shipping booked on both
                // sides, no extra costs. Every row the board priced before categories existed lands
                // here, and lands on exactly the numbers it did before.
                economics.ChannelLabel = "eBay · shipped";
                economics.FeeBasis = FeeBasisText(baseFees);
                economics.ShipsToBuyer = true;
                economics.Note = "Costed as a parcel: eBay's percentage fee, plus what it costs to box and post it.";

                return new CategoryQuote(
                    baseFees, avgCompShipping, avgCompShipping > 0 ? avgCompShipping : null, 0m, economics);
            }
        }
    }

    private static string FeeBasisText(FeeProfile fees) =>
        $"{fees.EbayFinalValueFeePercent:0.##}% + {fees.EbayFinalValueFeeFixed:C} final value fee";
}
