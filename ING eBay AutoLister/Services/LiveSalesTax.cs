using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The fourth thing winning a live lot costs: <b>sales tax</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The question this answers.</b> Every dollar on this card hangs off a landed cost, and the card
/// has been assembling it out of three things: the bid, the platform's premium, and the shipping.
/// A live marketplace in the United States charges a fourth. It is a marketplace facilitator, so it
/// collects the buyer's combined state and local sales tax at checkout and remits it; the buyer does
/// not get to decline it, and it is on the same receipt as the hammer price.
/// </para>
/// <para>
/// <b>It is the size of the premium.</b> The buyer's premium this card already charges is around 8%.
/// Combined US sales tax averages about 7.5% and runs past 10% in parts of Louisiana, Tennessee and
/// Alabama. So the ceiling has been leaving out a cost the size of the largest one it charges — and
/// leaving it out in the one direction that matters, because a ceiling that is too high is a ceiling
/// that says <i>bid</i> on a lot the seller then loses money on. Every other read on this card either
/// lowers the ceiling for measured evidence or raises it for a stated fact; this one lowers it for
/// arithmetic that is not in dispute.
/// </para>
/// <para>
/// <b>Which is why it is a state and not a rate.</b> A reseller can be exempt. Whatnot accepts a
/// resale certificate, and a seller who has filed one pays no sales tax on anything bought to resell
/// — which is most of this app's users on most of these lots. Charging every card a default rate
/// would quietly refuse good lots for a cost the seller does not pay. So nothing is assumed: an empty
/// box charges nothing and says so loudly, a ticked certificate charges nothing and says why, and a
/// typed rate is charged exactly as typed.
/// </para>
/// <para>
/// <b>What it is charged on.</b> The hammer plus the premium, and not the shipping. That is
/// <see cref="LotAnalyzer.CostOf"/>'s rule, which is the only sales-tax arithmetic this app has ever
/// had, and one app should have one. It is also the honest reading of a rule that genuinely differs
/// by state: roughly half of them tax delivery charges and the rest do not, and taxing shipping here
/// would be inventing a charge for the sellers in the states that do not. The cost of the choice is
/// bounded and known — it under-charges by the tax on one parcel, which on a $12 box at 7.5% is
/// ninety cents.
/// </para>
/// <para>
/// Pure, and it costs no lookup and no clock: everything it reads is on the request. So it re-answers
/// on a held-comps re-price exactly as it does on a fresh price, which is what lets a seller tick the
/// certificate box mid-lot and watch the ceiling move.
/// </para>
/// </remarks>
public static class LiveSalesTax
{
    /// <summary>
    /// Nothing above the highest combined rate any US jurisdiction charges. Shared with
    /// <see cref="RetailBuyCosts"/> rather than restated: a typo of 75 for 7.5 has to be caught the
    /// same way on the live card as it is on the deal board.
    /// </summary>
    public const decimal MaxRatePercent = RetailBuyCosts.MaxSalesTaxPercent;

    /// <summary>
    /// Roughly the US average combined state + local rate. Used for exactly one thing — sizing the
    /// silence in the <see cref="LiveTaxVerdicts.None"/> state — and never charged to a ceiling.
    /// Nothing on this card is ever priced at an average when the seller can type their own.
    /// </summary>
    public const decimal TypicalRatePercent = RetailBuyCosts.DefaultSalesTaxPercent;

    /// <summary>
    /// A stated rate, clamped. Null and zero both come back as zero — the difference between them is
    /// carried by <see cref="LiveTaxRead.Stated"/>, because "no sales tax in Oregon" and "nobody
    /// asked me" are the same number and completely different cards.
    /// </summary>
    public static decimal Sanitize(decimal? raw) =>
        raw is not decimal value || value <= 0m ? 0m : Math.Min(value, MaxRatePercent);

    /// <summary>What the platform adds in tax at a given bid, charged on the hammer plus the
    /// premium. The one multiplication in this file.</summary>
    public static decimal TaxOn(decimal taxableBase, decimal ratePercent) =>
        taxableBase <= 0m || ratePercent <= 0m ? 0m : Math.Round(taxableBase * ratePercent / 100m, 2);

    /// <summary>
    /// Reads what sales tax does to this lot.
    /// </summary>
    /// <param name="ratePercent">The seller's combined rate, as they entered it. Null is "nobody
    /// said"; a typed 0 is "no sales tax where I buy".</param>
    /// <param name="exempt">Whether a resale certificate is on file with the platform. Outranks any
    /// rate entered — an exempt buyer is charged nothing whatever their state's rate is.</param>
    /// <param name="currentBid">The bid on screen, for the cash figure. Zero before the bidding
    /// starts, which costs the strip its dollar figure and nothing else.</param>
    /// <param name="buyerFeePercent">The premium, already sanitized by the caller. Part of the base
    /// because the platform bills tax on what it charges, not on what the hammer said.</param>
    public static LiveTaxRead Read(
        decimal? ratePercent, bool exempt, decimal currentBid, decimal buyerFeePercent)
    {
        var stated = ratePercent is not null;
        var rate = Sanitize(ratePercent);
        var bid = Math.Max(0m, currentBid);
        var premium = Math.Max(0m, buyerFeePercent);
        var basis = Math.Round(bid * (1m + premium / 100m), 2);

        var read = new LiveTaxRead
        {
            Stated = stated,
            Exempt = exempt,
            TaxableBase = basis,
            // Charged nothing until something says otherwise. Only the last branch below raises it,
            // which is what makes "a card with an empty tax box is priced exactly as it was before
            // this file existed" a property of the code rather than a promise.
            RatePercent = 0m,
        };

        Judge(read, rate, basis);

        read.Charged = TaxOn(basis, read.RatePercent);
        // The tax multiplies the whole landed cost, so the highest affordable bid divides by
        // (1 + rate) — which is a smaller bite than the rate itself. Quoting the rate here would
        // overstate what this block costs the seller, on the one figure they use to judge it.
        read.CutPercent = read.RatePercent > 0m
            ? Math.Round(read.RatePercent / (100m + read.RatePercent) * 100m, 1)
            : 0m;

        return read;
    }

    /// <summary>Which of the four states this is, and every sentence it says in that state.</summary>
    private static void Judge(LiveTaxRead read, decimal rate, decimal basis)
    {
        // The certificate first, because it is true regardless of what is in the rate box — a
        // seller who filed one and then typed their state's rate is still exempt, and a card that
        // charged them for it would be refusing lots over a cost they do not pay.
        if (read.Exempt)
        {
            read.Verdict = LiveTaxVerdicts.Exempt;
            read.Headline = "No tax — resale certificate";
            read.Note = "You've told the app a resale certificate is on file, so the ceiling above is costed "
                      + "with no sales tax on the win. Worth being sure it really is filed with the platform "
                      + "and not just with your state — the marketplace collects it until the certificate is "
                      + "on their side, and it comes off this margin if it isn't.";
            return;
        }

        // Nothing entered. The ceiling above is charging no tax, which is right for an exempt buyer
        // and wrong by roughly the premium for everybody else — so this says how big the silence is
        // rather than filling it in. An assumed rate would be a cost nobody agreed to, taken off the
        // one number on the card the seller acts on.
        if (!read.Stated)
        {
            read.Verdict = LiveTaxVerdicts.None;
            read.Exposure = TaxOn(basis, TypicalRatePercent);
            read.Headline = "Sales tax not entered";
            read.Note = "The ceiling above is costing this lot as though no sales tax were charged on it. Live "
                      + "marketplaces collect it at checkout like any other seller — put your combined rate in "
                      + "the Sales tax box, or tick Resale cert if you have one on file and genuinely pay none.";
            read.Warning = read.Exposure > 0m
                ? $"No sales tax entered. At the {TypicalRatePercent:0.#}% US average that is about "
                + $"{read.Exposure:C} on this bid, straight off the margin — enter your rate, or tick "
                + "Resale cert if you have one filed."
                : "No sales tax entered. A live marketplace collects it on the winning bid and the premium, "
                + "and it comes straight off this margin — enter your rate, or tick Resale cert if you have "
                + "one filed.";
            return;
        }

        // A stated zero. Five states charge no sales tax, and a seller in one of them typing 0 is
        // giving a real answer that happens to cost nothing — so it gets its own state rather than
        // being folded into the silence above, which carries a warning it does not deserve.
        if (rate <= 0m)
        {
            read.Verdict = LiveTaxVerdicts.Free;
            read.Headline = "No sales tax at 0%";
            read.Note = "You've entered a rate of zero, so nothing is charged on the win. That is the right "
                      + "answer in the five states that levy no sales tax; anywhere else the marketplace "
                      + "collects it and this ceiling is higher than it should be.";
            return;
        }

        // The one state that moves money.
        read.Verdict = LiveTaxVerdicts.Charged;
        read.RatePercent = rate;

        var tax = TaxOn(basis, rate);
        read.Headline = tax > 0m
            ? $"{tax:C} tax at {rate:0.##}%"
            : $"Taxed at {rate:0.##}%";

        read.Note = basis > 0m
            ? $"Charged on the {Money(basis)} the platform bills you — the hammer and the premium on it, not "
            + "the shipping, which is how this app has costed an auction's tax since the liquidation board. "
            + "The ceiling above is what you can bid with that tax already inside it."
            : $"Once there is a bid, {rate:0.##}% of it and of the premium on it goes on the receipt. The "
            + "ceiling above is already costed with that tax in it.";
    }

    /// <summary>Every dollar figure on this strip, in one place. Cents are shown when there are any:
    /// a taxable base is routinely $108.00 and routinely $107.55, and rounding the second one away
    /// would misstate the figure the tax was worked out from.</summary>
    private static string Money(decimal amount) =>
        amount == Math.Truncate(amount) ? amount.ToString("C0") : amount.ToString("C");
}
