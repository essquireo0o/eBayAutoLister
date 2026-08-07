using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// What the queue costs: how long this lot's units wait behind the ones the seller already has,
/// and what the measured price slide takes off them across that wait.
/// </summary>
/// <remarks>
/// <para>
/// <b>The question this answers.</b> <see cref="LiveStockDepth"/> counts the pile and says how many
/// months it takes to clear, and it deliberately takes nothing off any price. Its reasoning was
/// right: a "you already have three" haircut is a number nobody measured, and the fourth one really
/// does resell for what the comps say — <i>it just sells in April</i>.
/// </para>
/// <para>
/// The half of that sentence nobody priced is <b>April</b>. "It resells for what the comps say" is
/// only true if the comps still say it in April, and this card already measures whether they will.
/// <see cref="LiveTrendRead.SlopePerMonth"/> is a Theil–Sen line in dollars per month fitted across
/// every dated sale. A measured slide times a measured wait is not a rule of thumb about duplicates
/// — it is two figures already on the screen, multiplied.
/// </para>
/// <para>
/// <b>So this is not a duplicate haircut.</b> A pile of a product whose price is flat costs nothing
/// here, however deep it gets — and a pile of a <i>climbing</i> product costs nothing either. What
/// costs is holding a <b>sliding</b> product long enough for the slide to happen to it. Those are
/// completely different claims, and only the second one has evidence behind it.
/// </para>
///
/// <para><b>How it differs from the trend cut, which is why the two stack rather than double-count.</b>
/// <see cref="LiveTrend"/> looks <i>backwards</i>: it re-bases the price from a median across the
/// whole sold history down to what these have been fetching in the last thirty days. That is a
/// correction to <b>today's</b> price. This looks <i>forwards</i> from today across the months the
/// seller's own unit will actually spend on a shelf before it sells. One says "today is not what
/// the ninety-day median claims"; the other says "and you are not selling today". Neither figure
/// is inside the other, and a test asserts a card with no wait is priced identically to one built
/// before this file existed.</para>
///
/// <para><b>The asymmetry, which is the same one the other two cuts are built around.</b> A
/// measured slide across a real wait <b>cuts</b> the ceiling. A measured climb across a real wait
/// does <b>not</b> raise it. Waiting is not a strategy for making money, and a ceiling raised
/// because the item might be dearer by the time it sells is a ceiling that pays today for a price
/// that has not happened — on a screen with seconds and one hammer.</para>
///
/// <para><b>What it refuses to do.</b></para>
/// <list type="bullet">
/// <item><description><b>Charge for a pile on its own.</b> Depth with no measured slide under it is
/// <see cref="LiveHoldVerdicts.Steady"/> or <see cref="LiveHoldVerdicts.Blind"/> and takes nothing
/// off. The pile is <see cref="LiveStockDepth"/>'s to report, and it still reports it in months
/// rather than dollars.</description></item>
/// <item><description><b>Project the line further than the sales it was fitted to.</b>
/// <see cref="PriceTrendAnalyzer.SlopePerMonth"/> is measured over the dated comps inside two
/// windows — about two months of sales. So the wait is priced across at most
/// <see cref="MaxProjectedMonths"/>, which is exactly that span. A year of shelf is charged two
/// months of slide and the strip says so; a line fitted to sixty days of sales knows nothing about
/// next spring.</description></item>
/// <item><description><b>Price a thin reading.</b> The slide must be <c>confirmed</c> — the same
/// bar <see cref="LiveTrend"/> requires before it may cut — and the window comparison must not be
/// pointing the other way. Two readings of the same comps that disagree produce a caveat, not a
/// haircut.</description></item>
/// <item><description><b>Read silence as a queue.</b> An unread sales book contributes nothing to
/// the units ahead. The count is the same one the stock strip shows, so the two can never disagree
/// about the same pile.</description></item>
/// </list>
///
/// <para><b>It costs no lookup and no clock.</b> The clearance rate and the slide are the card's own
/// figures, already computed; the shelf count arrives with the seller's own record and tonight's
/// count is one cached JSON read. So this re-answers on a held-comps re-price exactly as it does on
/// a fresh one, in the milliseconds a climbing bid leaves. Pure and deterministic — same inputs,
/// same ceiling, with no <c>DateTime.UtcNow</c> anywhere in it.</para>
/// </remarks>
public static class LiveHoldCost
{
    /// <summary>
    /// The shortest wait worth pricing. Under a month of extra shelf time, a slide of the size this
    /// app can measure at all is inside the noise of the comps it was measured from, and a card that
    /// shaved a dollar off every second unit would spend the seller's trust on nothing.
    /// </summary>
    public const decimal MinWaitMonths = 1m;

    /// <summary>
    /// The furthest the slide is ever projected — deliberately the span of sales the slide was
    /// measured across (<see cref="LiveTrend.WindowDays"/>, twice), and not a number of its own.
    /// </summary>
    /// <remarks>
    /// <see cref="PriceTrendAnalyzer.SlopePerMonth"/> fits its line to the dated comps inside those
    /// two windows. Extending that line four times its own length would be inventing evidence, and
    /// inventing it in the direction that refuses lots. A pile that takes a year to clear is charged
    /// two months of slide, and <see cref="LiveHoldRead.Capped"/> says out loud that the real
    /// exposure is longer than the figure on the strip.
    /// </remarks>
    public static decimal MaxProjectedMonths => LiveTrend.WindowDays * 2m / 30m;

    /// <summary>
    /// The most the ceiling is ever cut for the wait, however steep the line.
    /// </summary>
    /// <remarks>
    /// Deliberately lower than <see cref="LiveTrend.MaxHaircutPercent"/>. That one prices sales that
    /// actually happened; this one prices sales that have not. A projection must never be allowed to
    /// take more off a ceiling than a measurement.
    /// </remarks>
    public const decimal MaxHaircutPercent = 20m;

    /// <summary>
    /// Below this the cut is not taken at all. A slide of forty cents a month across a fourteen-month
    /// queue is arithmetic rather than evidence, and a ceiling shaved half a percent is a ceiling the
    /// seller cannot see the reason for — which spends their trust in the block for nothing.
    /// </summary>
    public const decimal MinCutPercent = 1m;

    /// <summary>
    /// Reads the queue in front of this lot and what the wait costs it.
    /// </summary>
    /// <param name="lotUnits">Units in the lot on screen — <see cref="LiveLotUnits.Count"/>.</param>
    /// <param name="own">The seller's own record, for the shelf count alone. Null means their book
    /// could not be read, which contributes nothing rather than being counted as a zero.</param>
    /// <param name="tonight">What tonight's buy sheet already holds of it.</param>
    /// <param name="monthlySales">How many of these eBay clears a month. Zero means nothing dated.</param>
    /// <param name="perUnitResale">The price the ceiling is being built from, AFTER the trend and
    /// condition cuts. Null or zero means there is nothing to erode.</param>
    /// <param name="trend">The trend read off the same comps. Null is treated as unreadable.</param>
    public static LiveHoldRead Read(
        int lotUnits, OwnSalesEvidence? own, LiveStockTonight tonight,
        decimal monthlySales, decimal? perUnitResale, LiveTrendRead? trend)
    {
        var lot = Math.Max(1, lotUnits);
        // The same two counts the stock strip draws as bars, added the same way, so the strip and
        // this can never disagree about one pile.
        var ahead = Math.Max(0, own?.UnitsHeld ?? 0) + Math.Max(0, tonight.Units);

        var read = new LiveHoldRead
        {
            LotUnits = lot,
            UnitsAhead = ahead,
            UnitsAfter = ahead + lot,
            MonthlySales = monthlySales > 0m ? monthlySales : 0m,
        };

        // No queue at all. Said rather than left blank, and said before anything else is looked at:
        // this is nearly every card, and on it the wait is genuinely zero rather than unmeasured.
        if (ahead == 0 && lot <= 1)
        {
            read.Verdict = LiveHoldVerdicts.Solo;
            read.Readable = true;
            read.WaitMonths = 0m;
            read.Headline = "Nothing of these queued ahead of it";
            read.Note = "It is the only one you'd have, so it sells at the market's own speed rather than "
                      + "waiting behind stock you already own.";
            read.MoneyNote = "The ceiling below is priced at what these fetch now, which is when this one sells.";
            return read;
        }

        // A queue and no rate to measure it in. The stock strip calls this state blind too, and for
        // the same reason: without a clearance rate there is no way to turn units into months.
        if (read.MonthlySales <= 0m)
        {
            read.Verdict = LiveHoldVerdicts.None;
            read.Headline = $"{Units(read.UnitsAfter)} of these, and no rate to say how long the last waits";
            read.Note = "No dated sold history, so there is no way to put a number of months on the queue — "
                      + "and no way to say what the price does across it either.";
            read.MoneyNote = "The ceiling below is priced at what these fetch now. Nothing here can say whether "
                           + "that is still the price by the time yours reaches the front.";
            return read;
        }

        // How long this lot's own units wait. Not the whole pile's clearing time: the seller is
        // deciding about THIS lot, and its units are the ones at the back of the queue.
        //
        // Unit number i out of the stack waits (i-1)/rate months longer than a unit with nothing in
        // front of it — the same arithmetic LiveLotSize.Absorption uses for the last unit's day
        // count, so the strip above and this cannot disagree. This lot occupies positions
        // ahead+1 .. ahead+lot, whose average is `ahead + (lot-1)/2`.
        //
        // Rounded to one place HERE, before it is multiplied by anything, rather than kept exact and
        // rounded for display. This is the figure the strip prints and the figure the seller checks
        // the cut against, and a cut worked out from 1.75 months while the screen says 1.8 is a cut
        // that cannot be reproduced by the person it is being charged to.
        var wait = Math.Round((ahead + (lot - 1) / 2m) / read.MonthlySales, 1);
        read.WaitMonths = wait;
        read.Readable = true;

        if (wait < MinWaitMonths)
        {
            read.Verdict = LiveHoldVerdicts.Quick;
            read.Headline = $"Yours sells in {Span(wait)} — the market clears them that fast";
            read.Note = $"{Rate(read)}, so {Units(read.UnitsAfter)} queue up for less than a month. That is "
                      + "too short a wait for a price move to happen across it.";
            read.MoneyNote = "The ceiling below is priced at what these fetch now, which is near enough when "
                           + "yours sells.";
            return read;
        }

        // A real wait. From here on the only question is what the price does across it.
        var projected = Math.Min(wait, MaxProjectedMonths);
        read.ProjectedMonths = projected;
        read.Capped = wait > MaxProjectedMonths;

        if (perUnitResale is not decimal price || price <= 0m)
        {
            read.Verdict = LiveHoldVerdicts.None;
            read.Headline = $"Yours sells {Span(wait)} out, at a price nothing here priced";
            read.Note = $"{Rate(read)}, so {Units(read.UnitsAfter)} of them is a queue this lot joins at the "
                      + "back. There is no resale figure on this card to say what that wait is worth.";
            read.MoneyNote = "No resale price was produced, so nothing was taken off anything.";
            return read;
        }

        if (trend is not { Readable: true })
        {
            read.Verdict = LiveHoldVerdicts.Blind;
            read.Headline = $"Yours sells {Span(wait)} out, and nothing dated says at what price";
            read.Note = $"{Rate(read)}, so this lot waits {Span(wait)} behind stock you already have. "
                      + "There is not enough dated sold history to say which way the price moves across it.";
            read.MoneyNote = "The ceiling below is priced at what these fetch now. Whether that is still the "
                           + $"price in {Span(wait)} is the part nothing here measured.";
            return read;
        }

        // A climb is never allowed to raise the ceiling for a longer hold. See the class remarks:
        // waiting is not a way of making money, and paying today for a price that has not happened
        // is how a good read loses cash on a purchase with no undo.
        if (trend.Direction == LiveTrendDirections.Rising)
        {
            read.Verdict = LiveHoldVerdicts.Steady;
            read.Headline = $"Yours sells {Span(wait)} out, into a rising price";
            read.Note = $"{Rate(read)}, so this lot waits {Span(wait)} behind stock you already have — "
                      + "and these have been getting dearer, not cheaper, over that sort of span.";
            read.MoneyNote = "The ceiling below is priced at what these fetch now. A climb never raises it: "
                           + "bidding up on a price the wait might deliver is paying for it twice.";
            return read;
        }

        // The line across every dated sale, which is the instrument for this question. The window
        // comparison sees two medians and says what has happened; the slope is a rate, and a rate is
        // the only thing that can be multiplied by a number of months.
        if (trend.SlopePerMonth is not decimal slope || slope >= 0m)
        {
            read.Verdict = LiveHoldVerdicts.Steady;
            read.Headline = $"Yours sells {Span(wait)} out, at about today's price";
            read.Note = $"{Rate(read)}, so this lot waits {Span(wait)} behind stock you already have. "
                      + "The trend line across every dated sale is not falling, so the wait costs nothing "
                      + "beyond the months themselves.";
            read.MoneyNote = "The ceiling below is priced at what these fetch now, and nothing measured says "
                           + "that changes over the wait.";
            return read;
        }

        var decline = Math.Round(-slope, 2);
        read.DeclinePerMonth = decline;

        // The same bar the trend read must clear before it may cut. A slide that is not firm enough
        // to price backwards is certainly not firm enough to price forwards.
        if (trend.Reliability != "confirmed")
        {
            read.Verdict = LiveHoldVerdicts.Unsure;
            read.Headline = $"Yours sells {Span(wait)} out, into a price that may be sliding";
            read.Note = $"{Rate(read)}, so this lot waits {Span(wait)} behind stock you already have, and "
                      + $"the trend line is drifting down about {Money(decline)} a month. On {trend.RecentSold} "
                      + $"recent and {trend.PriorSold} earlier sales that is not firm enough to price against.";
            read.MoneyNote = "The ceiling below is priced at what these fetch now — this read is too thin to cut "
                           + "it. Treat the wait as a reason to open the comps, not as a number.";
            read.Warning =
                $"This lot goes to the back of a queue {Span(wait)} long, and these look to be sliding about "
                + $"{Money(decline)} a month. The reading is too thin to price, so the ceiling below has NOT "
                + "been cut for it — it is the price these fetch today, not the price yours sells at.";
            return read;
        }

        // The measured cost of the wait. Not a judgement about how long the slide keeps going: it is
        // the rate the comps have actually been falling at, multiplied by the months this lot's own
        // units spend behind the seller's existing stock, and held at the span of sales the rate was
        // measured across.
        var erosion = Math.Round(decline * projected, 2);
        read.ErosionPerUnit = erosion;

        var raw = 1m - erosion / price;
        var floor = 1m - MaxHaircutPercent / 100m;
        var multiplier = Math.Round(Math.Max(floor, raw), 4);
        var cut = Math.Round((1m - multiplier) * 100m, 1);

        // A slide too small to matter across this wait leaves the price alone. Tested on the CUT
        // rather than on the dollars, so the percentage the strip prints and the price on the tile
        // can never disagree about whether anything happened.
        if (cut < MinCutPercent)
        {
            read.Verdict = LiveHoldVerdicts.Steady;
            read.Headline = $"Yours sells {Span(wait)} out, at about today's price";
            read.Note = $"{Rate(read)}, so this lot waits {Span(wait)}. These are sliding about "
                      + $"{Money(decline)} a month, which over that wait is too little to move the price.";
            read.MoneyNote = "The ceiling below is priced at what these fetch now — the measured slide is too "
                           + "small to matter across this wait.";
            return read;
        }

        read.ResaleMultiplier = multiplier;
        read.Floored = raw < floor;
        read.Discounted = true;
        read.CutPercent = cut;
        read.Verdict = LiveHoldVerdicts.Priced;

        read.Headline = $"Yours sells {Span(wait)} out — about {Money(erosion)} a unit lower by then";

        read.Note = $"{Rate(read)}, so {Units(read.UnitsAfter)} of them puts this lot {Span(wait)} back in "
                  + $"the queue. These have been sliding about {Money(decline)} a month across every dated sale, "
                  + $"which is about {Money(erosion)} a unit by the time yours reaches the front."
                  + (read.Capped
                      ? $" The queue runs longer than that; the slide is only projected {Span(MaxProjectedMonths)} "
                      + "out, because that is the span of sales it was measured across. The real exposure is longer "
                      + "than the figure above."
                      : "");

        read.MoneyNote =
            $"The ceiling below is priced {read.CutPercent:0.#}% under what these fetch today, at what the trend "
            + $"line says they fetch in {Span(projected)} — which is when yours sells, not today."
            + (read.Floored
                ? $" The projection measured further than that; the cut stops at {MaxHaircutPercent:0}%, because a "
                + "line fitted to two months of sales should never take more off a ceiling than the sales "
                + "themselves did."
                : "");

        read.Warning =
            $"You'd have {Units(read.UnitsAfter)} of these, so this lot sells {Span(wait)} from now — and "
            + $"they have been sliding about {Money(decline)} a month. That is about {Money(erosion)} a unit gone "
            + $"by the time yours sells, so the ceiling below is cut {read.CutPercent:0.#}% to bid at the price "
            + "yours actually gets. The comp table and the spread are today's sales.";

        return read;
    }

    /// <summary>
    /// The same resale figures scaled by the wait, or the original object when nothing was cut.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the three prices the ceiling is built out of move — expected sale, median and quick
    /// sale. The spread, the comp counts, the confidence, the sell-through and the clearance rate
    /// are copied across untouched: those describe sales that really happened, and scaling them
    /// would be inventing sales nobody made. The clearance rate especially — it is the figure the
    /// wait above was computed FROM, and a cut that quietly changed it would make the strip's own
    /// arithmetic unreproducible.
    /// </para>
    /// <para>
    /// Returning the <b>same instance</b> when nothing was cut is deliberate rather than an
    /// optimisation. It is what makes "a card with no queue behind it is priced exactly as it was
    /// before this file existed" a property of the code rather than a claim about it.
    /// </para>
    /// </remarks>
    public static ResalePricing Discount(ResalePricing resale, LiveHoldRead? hold)
    {
        if (hold is not { Discounted: true }) return resale;

        var multiplier = hold.ResaleMultiplier;
        if (multiplier <= 0m || multiplier >= 1m) return resale;

        return new ResalePricing
        {
            LookupTitle = resale.LookupTitle,
            Median = Scale(resale.Median, multiplier),
            ExpectedSale = Scale(resale.ExpectedSale, multiplier),
            QuickSale = Scale(resale.QuickSale, multiplier),
            SoldCompCount = resale.SoldCompCount,
            TerapeakCompCount = resale.TerapeakCompCount,
            SoldCompWeightPercent = resale.SoldCompWeightPercent,
            TerapeakWeightPercent = resale.TerapeakWeightPercent,
            PricedCompCount = resale.PricedCompCount,
            IdentityVerified = resale.IdentityVerified,
            AvgCompShipping = resale.AvgCompShipping,
            ConfidenceScore = resale.ConfidenceScore,
            ConfidenceLevel = resale.ConfidenceLevel,
            Basis = resale.Basis,
            DisagreementMessage = resale.DisagreementMessage,
            LiquidityScore = resale.LiquidityScore,
            LiquidityLevel = resale.LiquidityLevel,
            EstimatedDaysToSell = resale.EstimatedDaysToSell,
            EstimatedMonthlySales = resale.EstimatedMonthlySales,
            OpportunityScore = resale.OpportunityScore,
        };
    }

    private static decimal? Scale(decimal? price, decimal multiplier) =>
        price is > 0m ? Math.Round(price.Value * multiplier, 2) : price;

    /// <summary>The clearance rate, in the words the stock strip says it in.</summary>
    private static string Rate(LiveHoldRead read) => $"About {read.MonthlySales:0.#} of these sell a month on eBay";

    /// <summary>A span of months, in the app's one vocabulary for it — so "about 2.5 months" cannot
    /// become "roughly 3 months" one strip further up the card.</summary>
    private static string Span(decimal months) => LiveLotSize.MonthsInWords(months);

    /// <summary>
    /// A figure in this block, which is the one place on the card where the money can be small.
    /// A slide of $8.40 a month printed as "$8" is a tenth of itself thrown away on the item where
    /// it matters most; a slide of $140 printed as "$140.00" is two characters the eye has to skip
    /// under a countdown. So cents below ten dollars and none above it.
    /// </summary>
    private static string Money(decimal amount) =>
        Math.Abs(amount) < 10m ? amount.ToString("C2") : amount.ToString("C0");

    private static string Units(int count) => count == 1 ? "one" : count.ToString();
}
