namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The one thing a retail buy costs that a cash pickup doesn't: <b>sales tax</b>.
///
/// It matters more than it sounds. A $200 clearance item at 7.5% costs $215, and on a flip netting
/// $60 that tax is a quarter of the margin. Left out, every row on the deal-feed board would report
/// a profit the seller cannot actually make, and the rows nearest break-even — exactly the ones a
/// verdict flips on — would be the most wrong. Nothing else in this app has ever needed it, because
/// every other sourcing screen buys from a private seller.
/// </summary>
/// <remarks>
/// <para>
/// This deliberately does NOT live on <see cref="FeeProfile"/>. Everything there applies to every
/// item the app prices regardless of where it came from (eBay's cut, shipping, packaging, labour);
/// sales tax applies only to the retail half of one board, and putting it in the shared profile
/// would quietly start charging tax on Craigslist cash buys too.
/// </para>
/// <para>
/// The rate is the seller's, sent with the scan and remembered by the browser like the zip and
/// radius on the same form. The default is a middle-of-the-road US combined rate rather than zero,
/// because zero is a number no seller actually pays and defaulting to it would overstate every row.
/// Erring toward charging tax understates profit, which is the safe direction for a number someone
/// spends money on.
/// </para>
/// </remarks>
public static class RetailBuyCosts
{
    /// <summary>
    /// Roughly the US average combined state + local rate. A starting point that is wrong by a
    /// point or two for most sellers and right in spirit for all of them; the form lets them set
    /// their own, and a seller in one of the no-sales-tax states can set it to zero.
    /// </summary>
    public const decimal DefaultSalesTaxPercent = 7.5m;

    /// <summary>
    /// Nothing above the highest combined rate any US jurisdiction charges. A typo of 75 for 7.5
    /// would otherwise wipe out every deal on the board and report a genuine one as a loss.
    /// </summary>
    public const decimal MaxSalesTaxPercent = 15m;

    public static decimal Sanitize(decimal? percent) =>
        Math.Clamp(percent ?? DefaultSalesTaxPercent, 0m, MaxSalesTaxPercent);

    /// <summary>Tax on a sticker price, rounded to the cent the way a register would.</summary>
    public static decimal TaxOn(decimal stickerPrice, decimal taxPercent) =>
        stickerPrice <= 0 || taxPercent <= 0 ? 0m : Math.Round(stickerPrice * taxPercent / 100m, 2);

    /// <summary>What actually leaves the wallet — the cost basis every profit figure is built on.</summary>
    public static decimal AllInCost(decimal stickerPrice, decimal taxPercent) =>
        Math.Round(stickerPrice + TaxOn(stickerPrice, taxPercent), 2);

    /// <summary>
    /// The highest STICKER price that still breaks even, given that tax scales with it.
    ///
    /// Without tax, net profit falls exactly one dollar per dollar paid, so break-even is
    /// <c>ask + profit</c> — the identity the whole negotiation feature is built on. With tax it
    /// falls <c>(1 + rate)</c> dollars per sticker dollar, so the headroom is the profit divided by
    /// that. Quoting the untaxed number here would tell a seller to pay a price at which they lose
    /// money, which is the single most expensive thing a "max to pay" figure can get wrong.
    /// </summary>
    public static decimal BreakEvenSticker(decimal stickerPrice, decimal netProfit, decimal taxPercent)
    {
        var perDollar = 1m + taxPercent / 100m;
        return Math.Round(stickerPrice + netProfit / perDollar, 2);
    }
}
