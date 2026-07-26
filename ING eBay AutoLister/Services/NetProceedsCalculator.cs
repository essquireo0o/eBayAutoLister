using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Turns an asking price into the three numbers a seller actually needs: what they take home, the
/// price below which the sale costs them money, and the lowest offer worth accepting.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ProfitCalculator"/> already knew how to cost a sale, but only the sourcing screens
/// ever asked it. The screens where prices are actually set — the listing editor, the market
/// research panel, the sold-comps strip — showed gross numbers, so the app could recommend $120
/// and the seller could bank $91 and not know why. This wraps the same calculator in the shape
/// those screens need: itemised deductions to display, a floor to negotiate against, and a verdict
/// so a price below break-even looks like a warning rather than a figure.
/// </para>
/// <para>
/// Nothing here re-derives fee math. Break-even comes from ProfitCalculator, and the floors come
/// from the identity that falls out of it — <c>Net(P) = (P - breakEven) x KeepFraction</c> — which
/// is also what <see cref="WatcherOfferAdvisor"/> negotiates with, so the offer floor on the
/// watchers screen and the offer floor in the editor cannot drift apart.
/// </para>
/// </remarks>
public sealed class NetProceedsCalculator(ProfitCalculator profitCalc)
{
    /// <summary>Below this margin a sale is real but not worth sourcing more of.</summary>
    public const decimal ThinMarginPercent = 10m;

    /// <summary>A break-even past this is not a price, it is an unsolvable fee profile.</summary>
    private const decimal ImplausiblePrice = 1_000_000_000m;

    /// <summary>
    /// The lowest price that still leaves <paramref name="minNetProfit"/> in the seller's pocket
    /// after every fee, given the break-even.
    /// </summary>
    /// <remarks>
    /// Net profit is not a dollar-for-dollar function of price: eBay's cut scales with the sale,
    /// so buying back $10 of profit costs more than $10 of price. The price that yields a target
    /// profit is <c>breakEven + target / KeepFraction</c>.
    /// </remarks>
    public static decimal? ProfitFloorPrice(decimal? breakEvenPrice, decimal minNetProfit, FeeProfile fees)
    {
        if (breakEvenPrice is not decimal breakEven) return null;
        if (minNetProfit <= 0m) return Math.Round(breakEven, 2);

        var keep = fees.KeepFraction;
        // Fees alone swallow the sale — no price yields the target, so the floor is left at the
        // break-even rather than becoming an infinite number that silently blocks everything.
        if (keep <= 0m) return Math.Round(breakEven, 2);
        return Math.Round(breakEven + minNetProfit / keep, 2);
    }

    /// <summary>
    /// The lowest price whose net profit is still <paramref name="minMarginPercent"/> of gross.
    /// </summary>
    /// <remarks>
    /// A percentage floor and a dollar floor answer different questions — "is this worth my time"
    /// scales with the item, "is this worth the packing tape" does not — so a seller can set both
    /// and the binding one wins. Solving <c>(P - BE)k = m(P + S)</c> for P gives
    /// <c>P = (BE·k + m·S) / (k - m)</c>; when the target margin is at or above what the fees leave
    /// behind there is no such price, and the dollar floor is left to do the work alone.
    /// </remarks>
    public static decimal? MarginFloorPrice(
        decimal? breakEvenPrice, decimal minMarginPercent, decimal buyerPaidShipping, FeeProfile fees)
    {
        if (breakEvenPrice is not decimal breakEven) return null;
        if (minMarginPercent <= 0m) return null;

        var keep = fees.KeepFraction;
        var margin = minMarginPercent / 100m;
        if (keep <= margin) return null;

        return Math.Round((breakEven * keep + margin * buyerPaidShipping) / (keep - margin), 2);
    }

    /// <summary>Take-home on a sale at <paramref name="price"/>, or null with no break-even.</summary>
    public static decimal? NetProfitAt(decimal price, decimal? breakEvenPrice, FeeProfile fees) =>
        breakEvenPrice is decimal breakEven
            ? Math.Round((price - breakEven) * fees.KeepFraction, 2)
            : null;

    /// <summary>
    /// The binding floor for a negotiation and the rule that set it: never below break-even, and
    /// never below whichever of the seller's two policy floors bites harder.
    /// </summary>
    public static (decimal? Price, string Basis) MinimumOffer(
        decimal? breakEvenPrice, FeeProfile fees, decimal buyerPaidShipping = 0m,
        decimal? minNetProfitOverride = null, decimal? minMarginOverride = null)
    {
        if (breakEvenPrice is not decimal breakEven) return (null, "break_even");

        var minProfit = Math.Max(0m, minNetProfitOverride ?? fees.MinimumNetProfit);
        var minMargin = Math.Max(0m, minMarginOverride ?? fees.MinimumMarginPercent);

        var profitFloor = ProfitFloorPrice(breakEven, minProfit, fees) ?? breakEven;
        var marginFloor = MarginFloorPrice(breakEven, minMargin, buyerPaidShipping, fees);

        if (marginFloor is decimal m && m > profitFloor) return (Math.Round(m, 2), "margin_target");
        return (Math.Round(profitFloor, 2), minProfit > 0m ? "profit_target" : "break_even");
    }

    /// <summary>
    /// Everything one sale at <paramref name="askPrice"/> is worth, itemised.
    /// </summary>
    /// <param name="unitCost">
    /// What the seller paid for one unit. Null means they have not said — the fee side stays exact
    /// and the profit side is reported as an assumption rather than a fact.
    /// </param>
    public NetQuote Quote(
        decimal askPrice, decimal? unitCost, FeeProfile fees,
        decimal buyerPaidShipping = 0m, int quantity = 1,
        decimal? shippingCostOverride = null, decimal? otherCosts = null,
        decimal? minNetProfitOverride = null, decimal? minMarginOverride = null)
    {
        quantity = Math.Max(1, quantity);
        askPrice = Math.Max(0m, askPrice);
        buyerPaidShipping = Math.Max(0m, buyerPaidShipping);
        var cost = Math.Max(0m, unitCost ?? 0m);

        var breakdown = profitCalc.Calculate(
            supplierUnitCost: cost, quantity: quantity, expectedSalePrice: askPrice,
            quickSalePrice: askPrice, buyerPaidShipping: buyerPaidShipping, fees: fees,
            actualShippingCostOverride: shippingCostOverride, otherCosts: otherCosts);

        var gross = Math.Round(askPrice + buyerPaidShipping, 2);
        var totalFees = breakdown.MarketplaceFeeTotal;
        var totalDeductions = Math.Round(totalFees + breakdown.FulfilmentCostTotal, 2);

        var breakEven = breakdown.BreakEvenSalePrice < ImplausiblePrice
            ? (decimal?)breakdown.BreakEvenSalePrice : null;
        var (floor, basis) = MinimumOffer(breakEven, fees, buyerPaidShipping, minNetProfitOverride, minMarginOverride);

        var quote = new NetQuote
        {
            AskPrice = askPrice,
            BuyerPaidShipping = buyerPaidShipping,
            GrossRevenue = gross,
            Quantity = quantity,
            UnitCost = cost,
            HasCostBasis = unitCost is > 0m,

            EbayFinalValueFee = breakdown.EbayFees,
            PromotedListingFee = breakdown.PromotedListingFees,
            PaymentProcessingFee = breakdown.PaymentProcessingFees,
            ReturnReserve = breakdown.ReturnReserve,
            TestingReserve = breakdown.TestingReserve,
            ShippingCost = breakdown.ActualShippingCost,
            PackagingCost = breakdown.PackagingCost,
            LaborCost = breakdown.LaborCost,
            OtherCosts = breakdown.OtherCosts,

            TotalFees = totalFees,
            TotalDeductions = totalDeductions,
            NetProceeds = Math.Round(gross - totalDeductions, 2),
            NetProfit = breakdown.NetProfitPerUnit,
            TotalNetProfit = breakdown.TotalPotentialProfit,
            MarginPercent = breakdown.MarginPercent,
            RoiPercent = breakdown.RoiPercent,
            FeeLoadPercent = gross > 0m ? Math.Round(totalDeductions / gross * 100m, 1) : 0m,

            BreakEvenPrice = breakEven ?? 0m,
            MinimumOfferPrice = floor ?? 0m,
            MinimumOfferBasis = basis,
            MinimumNetProfit = Math.Max(0m, minNetProfitOverride ?? fees.MinimumNetProfit),
            MinimumMarginPercent = Math.Max(0m, minMarginOverride ?? fees.MinimumMarginPercent),
            NetProfitAtMinimumOffer = NetProfitAt(floor ?? 0m, breakEven, fees) ?? 0m,
        };

        quote.BelowBreakEven = breakEven is decimal be && askPrice > 0m && askPrice < be;
        quote.BelowMinimumOffer = floor is decimal f && askPrice > 0m && askPrice < f;
        quote.Lines = BuildLines(quote);
        (quote.Verdict, quote.Headline, quote.Note) = Describe(quote);
        return quote;
    }

    // Every deduction, in the order money leaves the sale, including the ones that are currently
    // zero — see NetQuoteLine. Detail carries the rate so the number is checkable rather than
    // something the seller has to trust.
    private static List<NetQuoteLine> BuildLines(NetQuote q) =>
    [
        new("ebay_fvf", "eBay final value fee", q.EbayFinalValueFee, "Percentage of the sale plus the per-order fee"),
        new("promoted", "Promoted Listings", q.PromotedListingFee, "Ad rate on the total sale"),
        new("payment", "Payment processing", q.PaymentProcessingFee, "Processor's cut, when it is billed separately"),
        new("shipping", "Shipping label", q.ShippingCost, "What it costs you to ship, not what the buyer pays"),
        new("packaging", "Packaging", q.PackagingCost, "Box, filler, tape and label"),
        new("labor", "Handling", q.LaborCost, "Your time picking, packing and dropping off"),
        new("returns", "Returns reserve", q.ReturnReserve, "Set aside against the share of sales that come back"),
        new("testing", "Testing reserve", q.TestingReserve, "Set aside against units that fail testing"),
        new("other", "Other costs", q.OtherCosts, "Anything item-specific you added"),
    ];

    // The verdict drives colour and copy together, so a price that loses money can never be
    // rendered in the same style as one that makes it.
    private static (string Verdict, string Headline, string Note) Describe(NetQuote q)
    {
        if (q.AskPrice <= 0m)
            return ("no_price", "Enter a price", "Type an asking price to see what you actually take home.");

        var take = Money(q.NetProceeds);

        if (!q.HasCostBasis)
            return ("no_cost_basis", $"{take} lands in your account",
                $"After {Money(q.TotalDeductions)} of fees and shipping on a {Money(q.GrossRevenue)} sale. "
                + "Add what you paid for this item to see profit, break-even and your offer floor.");

        var profit = Money(q.NetProfit);

        if (q.NetProfit < 0m)
            return ("loss", $"You lose {Money(Math.Abs(q.NetProfit))} on this sale",
                $"Fees and shipping take {Money(q.TotalDeductions)}, and the item cost you {Money(q.UnitCost)}. "
                + $"You need {Money(q.BreakEvenPrice)} just to break even.");

        if (q.BelowMinimumOffer)
            return ("below_floor", $"{profit} profit — under your floor",
                $"You set a floor of {FloorPolicy(q)}, which needs {Money(q.MinimumOfferPrice)}. "
                + "This price clears costs but not the bar you set.");

        if (q.MarginPercent is decimal margin && margin < ThinMarginPercent)
            return ("thin", $"{profit} profit — thin at {margin:0.#}%",
                $"Fees and shipping take {Money(q.TotalDeductions)} of a {Money(q.GrossRevenue)} sale. "
                + $"One return wipes this out; break-even is {Money(q.BreakEvenPrice)}.");

        return ("profitable", $"{profit} profit in your pocket",
            $"{Money(q.GrossRevenue)} sale, {Money(q.TotalDeductions)} to fees and shipping, "
            + $"{Money(q.UnitCost)} cost. Break even at {Money(q.BreakEvenPrice)}; "
            + $"don't accept less than {Money(q.MinimumOfferPrice)}.");
    }

    private static string FloorPolicy(NetQuote q) => q.MinimumOfferBasis switch
    {
        "margin_target" => $"{q.MinimumMarginPercent:0.#}% margin",
        "profit_target" => $"{Money(q.MinimumNetProfit)} profit per sale",
        _ => "break-even",
    };

    private static string Money(decimal value) => value.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
}
