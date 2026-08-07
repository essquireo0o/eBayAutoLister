using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The last thing standing between a good call and a bad night: <b>the money that is actually
/// there</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The question this answers.</b> Every other read on this card asks what the thing on screen is
/// worth, and sixteen sessions have gone into sharpening that: what these fetch lately, what they
/// fetch in this condition, what they fetch when yours reaches the front of the queue, what the
/// platform bills on top. All of it assumes the seller can pay whatever the answer comes to.
/// </para>
/// <para>
/// A live show breaks that assumption faster than any other sourcing channel in this app. The lots
/// come every four minutes, each one individually defensible, and the card says <c>BID UP TO</c> on
/// every one of them. Six good calls in a row is a seller who has committed $1,400 they did not have
/// — on stock that becomes cash in forty days, if it sells. Nothing on this screen has ever counted
/// the money leaving; the buy sheet knew it exactly, and was only ever read after the hammer.
/// </para>
/// <para>
/// <b>It is not a haircut and it is not a judgement.</b> Every other cut on this card comes off the
/// resale price, because it is a claim about what the object fetches. This one never touches the
/// resale price, the median, the spread, the sell-through or the comps: the item is worth exactly
/// what it was worth: there is simply not enough money left to land it. So it cuts the CEILING and
/// leaves the market alone, and it says so in cash — a seller told <c>DON'T BID</c> on a lot they
/// cannot afford would learn the wrong thing about the item and walk past the next one.
/// </para>
/// <para>
/// <b>The arithmetic is the card's own.</b> What is left has to cover the whole landed cost — the
/// bid, the premium on it, the tax on both and the freight — so turning the remaining cash into a
/// bid is exactly <see cref="LiveBidAdvisor.BreakEvenBid"/>, which is the function that already
/// turns an all-in ceiling into a bid on this screen. Nothing here divides by <c>(1+fee)(1+tax)</c>
/// a second time.
/// </para>
/// <para>
/// <b>Nothing is assumed.</b> An empty budget box caps nothing, whatever is on the sheet, and the
/// card is priced byte-for-byte as it was before this file existed. The spend is still reported in
/// that state, because what has already left the account is a fact and not a setting.
/// </para>
/// <para>
/// Pure, and it costs no lookup and no clock: the budget is on the request and the spend is one
/// cached read of a list already in memory. So it re-answers on a held-comps re-price exactly as it
/// does on a fresh price — which is what lets a seller raise their budget mid-lot and watch the
/// ceiling come back.
/// </para>
/// </remarks>
public static class LiveBudget
{
    /// <summary>
    /// A budget past this is a typo — a fat-fingered extra zero on a night's buying. Clamped rather
    /// than rejected, so a stray keystroke costs a cap that never binds instead of the answer.
    /// </summary>
    public const decimal MaxBudget = 1_000_000m;

    /// <summary>
    /// What <see cref="LiveBidCard.CeilingBoundBy"/> says when the cash is the binding constraint —
    /// alongside the sniper's own two, which are both bars about the item. Spelled here because this
    /// is the file that sets it.
    /// </summary>
    public const string CeilingByBudget = "budget";

    /// <summary>
    /// Past this share of the budget, "how much is left" stops being background and goes on the
    /// warning list. Three quarters gone is the point where the next two lots decide the night.
    /// </summary>
    public const decimal TightPercent = 75m;

    /// <summary>A stated budget, clamped. Null and zero are both "no budget" — nothing here caps a
    /// ceiling for a number nobody typed.</summary>
    public static decimal Sanitize(decimal? raw) =>
        raw is not decimal value || value <= 0m ? 0m : Math.Min(value, MaxBudget);

    /// <summary>
    /// Reads what tonight's money does to this lot.
    /// </summary>
    /// <param name="budget">What the seller set aside for tonight. Null or zero caps nothing.</param>
    /// <param name="tonight">What the buy sheet has already committed
    /// (<see cref="LiveBuySheet.Committed"/>). Default is nothing, so a card built without a sheet
    /// is priced identically.</param>
    /// <param name="marketCeiling">The ceiling the comps produced, before the cash was considered.
    /// Zero or less means the market already refused this lot, and nothing here overrules that.</param>
    /// <param name="buyerFeePercent">The premium, already sanitized by the caller.</param>
    /// <param name="shipping">What this lot is charged to get delivered — the marginal figure
    /// <see cref="LiveShipShare"/> settled on, because that is what winning it actually costs.</param>
    /// <param name="salesTaxPercent">The rate the ceiling above was charged at, so the cash ceiling
    /// and the market ceiling are costed on identical terms.</param>
    public static LiveBudgetRead Read(
        decimal? budget, LiveBudgetTonight tonight, decimal marketCeiling,
        decimal buyerFeePercent, decimal shipping, decimal salesTaxPercent)
    {
        var stated = Sanitize(budget) > 0m;
        var cap = Sanitize(budget);
        var committed = Math.Max(0m, tonight.Spent);
        var market = Math.Max(0m, marketCeiling);

        var read = new LiveBudgetRead
        {
            Stated = stated,
            Budget = cap,
            Committed = committed,
            LotsWon = Math.Max(0, tonight.Lots),
            Remaining = Math.Max(0m, cap - committed),
            MarketCeiling = market,
            // The ceiling stays the market's until a branch below says otherwise, which is what
            // makes "a card with an empty budget box is priced exactly as it was before this file
            // existed" a property of the code rather than a promise.
            Ceiling = market,
        };

        read.SpentPercent = cap > 0m ? Math.Round(Math.Min(100m, committed / cap * 100m), 1) : 0m;

        // What is left has to land the whole lot — the bid, the premium on it, the tax on both and
        // the freight. That is the same conversion the walk-away line already makes on this card, so
        // it is called rather than re-derived: two "what bid fits inside this much money" arithmetics
        // is how the badge and the ladder end up disagreeing by the premium.
        read.Affordable = stated
            ? LiveBidAdvisor.BreakEvenBid(read.Remaining, buyerFeePercent, shipping, salesTaxPercent)
            : 0m;

        Judge(read, shipping);

        read.CutPercent = read.Applied && market > 0m
            ? Math.Round((market - read.Ceiling) / market * 100m, 1)
            : 0m;

        return read;
    }

    /// <summary>Which of the four states this is, and every sentence it says in that state.</summary>
    private static void Judge(LiveBudgetRead read, decimal shipping)
    {
        // No budget. The ceiling above is the market's, which is right — and what has already been
        // spent tonight is still said out loud, because it is a fact about the account rather than
        // an assumption about the seller. An assumed budget would refuse lots over a limit nobody
        // set, on the one screen where a refused lot is gone in four minutes.
        if (!read.Stated)
        {
            read.Verdict = LiveBudgetVerdicts.None;
            read.Headline = read.Committed > 0m
                ? $"No budget set — {Money(read.Committed)} committed tonight"
                : "No budget set";
            read.Note = read.LotsWon > 0
                ? $"The ceiling above is what the item is worth, not what you have left. You have "
                + $"{Lots(read.LotsWon)} on tonight's sheet for {Money(read.Committed)} all in — set a "
                + "budget and the ceiling will never quote you a bid you cannot cover."
                : "The ceiling above is what the item is worth. Set a budget for tonight and it will "
                + "also be what you can afford — live shows put up a defensible lot every four "
                + "minutes, and six good calls in a row is a night's cash flow gone.";
            // Said only once there is real money on the sheet. A warning on the first lot of the
            // night would be a warning about nothing.
            read.Warning = read.Committed > 0m
                ? $"No budget set, and {Money(read.Committed)} is already committed tonight across "
                + $"{Lots(read.LotsWon)}. Nothing on this card is counting that."
                : "";
            return;
        }

        // The budget is gone. Two ways to get here and they are different sentences: the money is
        // spent, or what is left will not even pay the freight — which is the one a seller stares at
        // a $6 lot not understanding, so it names the shipping.
        if (read.Remaining <= 0m || read.Affordable <= 0m)
        {
            read.Verdict = LiveBudgetVerdicts.Spent;
            read.Ceiling = 0m;

            var nothingLeft = read.Remaining <= 0m;
            read.Headline = nothingLeft
                ? $"Budget spent — {Money(read.Committed)} of {Money(read.Budget)}"
                : $"{Money(read.Remaining)} left — not enough to land a lot";

            read.Note = nothingLeft
                ? $"Tonight's {Money(read.Budget)} is committed: {Lots(read.LotsWon)} at "
                + $"{Money(read.Committed)} all in. Nothing on this card says the lot is bad — the "
                + "market ceiling is still above, and it is still what the item is worth."
                : $"{Money(read.Remaining)} is left of {Money(read.Budget)}, and {Money(shipping)} of "
                + "any win goes on shipping before the hammer is paid at all. There is no bid this "
                + "covers.";

            read.Reason = read.MarketCeiling > 0m
                ? $"The item is worth up to {Money(read.MarketCeiling)} and you have "
                + $"{Money(read.Remaining)} left of tonight's {Money(read.Budget)}. Nothing wrong with "
                + "the lot — raise the budget if this is the one you came for."
                : "";

            read.Warning = $"Tonight's budget is spent — {Money(read.Committed)} of "
                         + $"{Money(read.Budget)} across {Lots(read.LotsWon)}. This ceiling is your "
                         + "cash, not the market's.";
            return;
        }

        // The cash is the ceiling. The one state that moves money, and the only one on this card
        // whose cut has nothing to do with the item.
        if (read.MarketCeiling > 0m && read.Affordable < read.MarketCeiling)
        {
            read.Verdict = LiveBudgetVerdicts.Capped;
            read.Ceiling = read.Affordable;

            read.Headline = $"{Money(read.Remaining)} left — that's the ceiling";
            read.Note = $"The comps put this at {Money(read.MarketCeiling)}, and {Money(read.Remaining)} "
                      + $"of tonight's {Money(read.Budget)} is left. The ceiling above is what that cash "
                      + "lands with the premium, the tax and the shipping already inside it — not what "
                      + "the item is worth.";
            read.Reason = $"That is your cash, not the comps: they put this at "
                        + $"{Money(read.MarketCeiling)}, and {Money(read.Remaining)} of tonight's "
                        + $"{Money(read.Budget)} is left.";
            read.Warning = $"This ceiling is your remaining {Money(read.Remaining)}, not the "
                         + $"{Money(read.MarketCeiling)} the comps support. Win it and tonight's budget "
                         + "is gone.";
            return;
        }

        // Room to spare. Says the figure and nothing else — a card that is right is not warned about.
        read.Verdict = LiveBudgetVerdicts.Clear;
        read.Headline = $"{Money(read.Remaining)} left of {Money(read.Budget)}";
        read.Note = read.LotsWon > 0
            ? $"{Lots(read.LotsWon)} won tonight for {Money(read.Committed)} all in. The ceiling above "
            + "is the market's — your cash covers it."
            : $"Nothing committed tonight yet. The ceiling above is the market's, and it is inside "
            + "tonight's budget.";
        // The one thing worth interrupting for in a state that cuts nothing: the budget is nearly
        // gone and the NEXT lot is where it stops the seller, not this one.
        read.Warning = read.SpentPercent >= TightPercent
            ? $"{read.SpentPercent:0.#}% of tonight's budget is committed — {Money(read.Remaining)} "
            + $"left of {Money(read.Budget)}. This lot fits; the one after it may not."
            : "";
    }

    /// <summary>Lots, counted in words, because "1 lots" on a live card is the sort of thing that
    /// makes a seller distrust the figure beside it.</summary>
    private static string Lots(int count) => count == 1 ? "1 lot" : $"{count} lots";

    /// <summary>Every dollar figure on this strip, in one place. Cents are shown when there are any:
    /// a night's spend is routinely $438.00 and routinely $437.65, and rounding the second one away
    /// would misstate the money the ceiling was worked out from.</summary>
    private static string Money(decimal amount) =>
        amount == Math.Truncate(amount) ? amount.ToString("C0") : amount.ToString("C");
}
