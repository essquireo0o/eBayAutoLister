using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The press, not the price: what the <b>next bid</b> costs, and how many more of them fit under the
/// ceiling before the lot stops being worth winning.
/// </summary>
/// <remarks>
/// <para>
/// Every figure on the live card compares the bid <i>on screen</i> against the ceiling, which answers
/// "was the last bid all right". That is not the question a live bidder has. Nobody buys at the price
/// on screen — pressing bid commits to the <b>next increment</b>, and between those two numbers is a
/// gap the card has never mentioned:
/// </para>
/// <para>
/// The bidding is at <b>$45</b>. The ceiling is <b>$46</b>. The card says <c>BID UP TO $46</c> with
/// <c>$1.00</c> of room, which is true, and the seller presses bid — and the bid becomes <b>$50</b>,
/// four dollars past the ceiling the rest of the card spent its arithmetic protecting. The ceiling
/// was real; the press was not. Nothing on the screen could tell them apart.
/// </para>
/// <para>
/// So this counts <b>presses</b>. Two more bids, one more bid, or none — the single most glanceable
/// number this screen can carry, because it is an integer, it does not need a currency symbol read
/// off it, and it is the thing the hand is about to do.
/// </para>
///
/// <para><b>Nothing here prices anything.</b> The ceiling is
/// <see cref="AuctionSniperAnalyzer.MaxBidDetail"/>'s and the landed cost is
/// <see cref="LiveBidAdvisor.LandedCost"/>'s, both unchanged. This file adds one input — how much the
/// bidding goes up by — and turns the ceiling already on the card into a count of the presses left
/// under it.</para>
///
/// <para><b>The increment is an assumption, and it says so.</b> No live platform publishes its bid
/// ladder and hosts change it mid-show, so the default here is a convention rather than a fact: the
/// same ladder the bid stepper has always used. It is stated on the card in the seller's own units
/// and it is overridable in one keystroke, because the seller is looking at a screen that shows the
/// next bid amount and this app is not. An assumed number that is quietly wrong is the failure mode
/// worth spending a line of the card on.</para>
///
/// <para><b>Everything rounds against the bidder.</b> The count is a floor, and a press that lands
/// exactly on the ceiling counts while one a cent over does not. The presses <i>after</i> the next
/// one are walked rather than divided, up the same ladder, because a show's increments grow with the
/// price and <c>room ÷ increment</c> would promise presses that will never be offered.</para>
///
/// <para><b>Except a step the seller stated, which is held flat.</b> They are watching the show and
/// this app is not: a typed step is used as typed, at every level, rather than being talked upwards
/// by an assumption. What makes that safe is that the count is not what anybody acts on — the
/// <i>next</i> press is always costed exactly, and the whole card is re-answered every time the bid
/// moves. The count is how many presses there is room for, and being wrong about the fourth one is
/// worth far less than quietly overruling a number somebody typed.</para>
///
/// <para>Pure and deterministic — no clock, no state, no network.</para>
/// </remarks>
public static class LiveBidIncrement
{
    /// <summary>The seller stated the increment. It outranks the ladder below, always.</summary>
    public const string SourceSeller = "seller";

    /// <summary>Nobody stated it, so the usual live-auction ladder was assumed.</summary>
    public const string SourceAssumed = "assumed";

    /// <summary>
    /// Where counting stops. A ceiling a hundred presses above the bid is not a decision anybody
    /// makes one press at a time, and the exact integer stops being the point long before then — the
    /// card says "40+" and the room figure carries the rest.
    /// </summary>
    public const int MaxBidsCounted = 40;

    /// <summary>
    /// A stated increment beyond this is a typo — someone typing the bid into the step box. Clamped
    /// rather than rejected, for the same reason the buyer's premium is: a stray keystroke should
    /// cost a wrong number the seller can see, not the whole answer.
    /// </summary>
    public const decimal MaxStatedIncrement = 5_000m;

    /// <summary>
    /// What one bid is worth at this level, when nobody has said. A live sale goes up in dollars at
    /// $12 and in twenties at $600, so a fixed step is wrong at one end and useless at the other.
    /// </summary>
    /// <remarks>
    /// This is the definition. The browser's <c>wnBidStep</c> is the same ladder so that the − / +
    /// buttons move the bid to the number the card calls the next bid, and an asset test pins the two
    /// together — a stepper that jumped to $50 under a card that said the next bid was $55 would be
    /// the app disagreeing with itself about the one figure this whole file is for.
    /// </remarks>
    public static decimal Assumed(decimal bid) => Math.Max(0m, bid) switch
    {
        < 25m => 1m,
        < 100m => 5m,
        < 500m => 10m,
        < 2_000m => 25m,
        _ => 100m,
    };

    /// <summary>
    /// The increment to answer with, and where it came from. A stated one is used exactly as typed —
    /// the seller can see the show's own next-bid amount and this app cannot.
    /// </summary>
    public static (decimal Increment, string Source) Sanitize(decimal? stated, decimal bid) =>
        stated is decimal typed && typed > 0m
            ? (Math.Min(Math.Round(typed, 2), MaxStatedIncrement), SourceSeller)
            : (Assumed(bid), SourceAssumed);

    /// <summary>
    /// How many presses fit from <paramref name="from"/> up to and including
    /// <paramref name="ceiling"/>, and what the first press past it would cost.
    /// </summary>
    /// <remarks>
    /// Walked rather than divided, because an assumed step is not constant: it grows as the price
    /// climbs, so <c>(ceiling − bid) / increment</c> would over-count every lot whose ceiling sits
    /// above the next rung of the ladder.
    /// </remarks>
    /// <param name="stated">
    /// True when the seller typed the step. It is then held flat all the way up rather than being
    /// talked upwards by the ladder — see the class remarks for why that is the right way round.
    /// </param>
    public static (int Count, bool Capped, decimal FirstOver) CountBids(
        decimal from, decimal ceiling, decimal increment, bool stated = false)
    {
        // Never seen from Sanitize, which cannot return a non-positive increment. Guarded anyway:
        // this loop is the one place in the live path where a zero would not produce a wrong number,
        // it would produce no answer at all.
        if (increment <= 0m) return (0, false, Math.Round(from, 2));

        var at = Math.Round(from, 2);
        var step = increment;
        var count = 0;

        while (count < MaxBidsCounted)
        {
            var next = Math.Round(at + step, 2);
            if (next > ceiling) return (count, false, next);

            count++;
            at = next;
            step = stated ? increment : Assumed(at);
        }

        return (count, true, Math.Round(at + step, 2));
    }

    /// <summary>
    /// The next press, read off a card that has already been priced.
    /// </summary>
    /// <param name="card">
    /// The card as far as the ceiling: <see cref="LiveBidCard.MaxBid"/>,
    /// <see cref="LiveBidCard.CurrentBid"/>, the premium and the shipping all set. Nothing here
    /// writes to it.
    /// </param>
    /// <param name="breakEvenAllIn">
    /// The all-in cost at which this lot stops making money — the same figure the ceiling was
    /// derived from, so the profit at the next bid and the profit at the ceiling are one subtraction
    /// apart and cannot disagree.
    /// </param>
    /// <param name="stated">What the seller typed in the bid-step box, when they typed one.</param>
    public static LiveNextBid Read(LiveBidCard card, decimal breakEvenAllIn, decimal? stated)
    {
        var (increment, source) = Sanitize(stated, card.CurrentBid);

        var read = new LiveNextBid { Increment = increment, IncrementSource = source };

        // The seller's own figure is echoed wherever the block appears, because it is a number they
        // typed that quietly outranks the app's and they are owed sight of it. The assumed one is
        // only stated once there is a bid for it to be assumed AT — a ladder read off a bid of zero
        // is the bottom rung, and printing it before the bidding starts would be the card asserting
        // an increment for a price nobody has named.
        if (source == SourceSeller)
            read.IncrementNote = $"Bids go up in {increment:C}, as you typed it.";

        // No ceiling to count to. The card already says DON'T BID in its badge, and a strip counting
        // presses under a ceiling of zero would be arithmetic about a lot nobody should touch.
        if (card.MaxBid <= 0m) return read;

        // Before the first bid there is no "next" one — the opening price is the host's to name, and
        // guessing it would put a dollar figure on the card that nothing on screen agrees with. The
        // strip still appears, saying that, because a block that is silent before the bidding starts
        // and silent when it cannot read anything is a block whose silence means two things.
        if (!card.BidWasKnown)
        {
            read.Headline = "Bidding hasn't started";
            read.Note = $"Once it does, this counts the presses left under the {card.MaxBid:C} ceiling.";
            return read;
        }

        if (source == SourceAssumed)
        {
            read.IncrementNote =
                $"Assuming the bidding goes up in {increment:C} at this level — type the show's own " +
                "next-bid amount in Bid step if it differs.";
        }

        read.Readable = true;
        read.Amount = Math.Round(card.CurrentBid + increment, 2);
        read.Landed = LiveBidAdvisor.LandedCost(
            read.Amount, card.BuyerFeePercent, card.ShippingCost, card.Tax.RatePercent);
        // Not clamped at zero, unlike ProfitAtMaxBid. A negative here is the whole point of the
        // figure: it is what the press would cost, and rendering it as $0.00 would turn the one
        // number that says "this press loses money" into one that says "this press makes none".
        read.Profit = Math.Round(breakEvenAllIn - read.Landed, 2);

        var landed = card.BuyerFeePercent > 0m || card.ShippingCost > 0m || card.Tax.Applied
            ? $" ({read.Landed:C} landed)"
            : "";

        // Already past it. Said plainly and without a count, because "0 more bids" on a lot that is
        // gone reads as a lot that is still in play.
        if (card.CurrentBid > card.MaxBid)
        {
            read.Verdict = LiveNextBidVerdicts.Over;
            read.Headline = "Already past it";
            read.Note = $"The bidding is {card.CurrentBid - card.MaxBid:C} over your {card.MaxBid:C} " +
                        $"ceiling. Pressing bid would make it {read.Amount:C}{landed}.";
            return read;
        }

        var (count, capped, firstOver) = CountBids(
            card.CurrentBid, card.MaxBid, increment, stated: source == SourceSeller);
        read.BidsLeft = count;
        read.BidsLeftCapped = capped;

        if (count == 0)
        {
            // The gap this file exists for: under the ceiling, and no press that stays under it.
            read.Verdict = LiveNextBidVerdicts.Stop;
            read.Headline = "Don't press";
            read.Note = $"The bidding is at {card.CurrentBid:C} and your ceiling is {card.MaxBid:C}, but " +
                        $"bids go up in {increment:C} — pressing makes it {read.Amount:C}, " +
                        $"{read.Amount - card.MaxBid:C} past it. There is no bid left to make on this lot.";
            read.Warning = $"Your ceiling is {card.MaxBid:C} and the bidding is only at {card.CurrentBid:C}, " +
                           $"but the next bid is {read.Amount:C} — past it. The room above the bid is real " +
                           "and there is no press that stays inside it.";
            return read;
        }

        if (count == 1)
        {
            read.Verdict = LiveNextBidVerdicts.Last;
            read.Headline = "Last bid";
            read.Note = $"{read.Amount:C}{landed} is the last bid under your {card.MaxBid:C} ceiling — " +
                        $"the one after it, at {firstOver:C}, is {firstOver - card.MaxBid:C} past it. " +
                        $"You clear {read.Profit:C} if it hammers here.";
            return read;
        }

        read.Verdict = LiveNextBidVerdicts.Press;
        read.Headline = capped ? $"{MaxBidsCounted}+ more bids" : $"{count} more bids";
        read.Note = $"Pressing makes it {read.Amount:C}{landed} for {read.Profit:C}. " +
                    $"{(capped ? "Plenty of room" : $"Room for {count} presses")} before your " +
                    $"{card.MaxBid:C} ceiling.";
        return read;
    }
}
