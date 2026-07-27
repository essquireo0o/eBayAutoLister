using System.Globalization;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The buy side. Works out what to open at on a local deal, what the ceiling is, and drafts the
/// message to send — anchored on the same sold comps that priced the flip.
/// </summary>
/// <remarks>
/// <para>
/// A dollar saved on the buy is worth more than a dollar added to the sale: it arrives immediately,
/// eBay takes no cut of it, nothing ships and nobody waits. Every other pricing screen here works
/// the sell side; this is the one that works the cheap side, and on a local pickup it is the only
/// lever there is.
/// </para>
/// <para>
/// Everything is pure and every number is borrowed rather than re-derived. The break-even buy price
/// arrives already costed by <see cref="ProfitCalculator"/>/<see cref="FeeProfile"/> (it is the same
/// <c>MaxBuyPrice</c> the arbitrage row shows), and net profit at any other price is that break-even
/// minus the price — exact, because net profit falls one dollar for every dollar more paid. The
/// "great buy" and "worth doing" bars are <see cref="LocalArbitrageAnalyzer"/>'s own goldmine and
/// solid thresholds, so a price this calls great is a price that board would have badged a goldmine.
/// </para>
/// <para>
/// The drafts are deliberately honest. They quote a resale figure only when there is enough sold
/// history to stand behind it, they never invent scarcity or a deadline, they never disparage the
/// item to justify the number, and they explain the offer with the real reason — fees, shipping and
/// the wait. A seller's own name goes on these messages.
/// </para>
/// </remarks>
public static class NegotiationAdvisor
{
    // ── The bars ─────────────────────────────────────────────────────────────────────────────
    // A great buy is the goldmine bar; a ceiling is the "solid, worth the drive" bar. Shared with
    // the board that ranks these listings, so there is one definition of each and not a friendlier
    // second one for the feature that does the talking.
    public const decimal GreatDealProfit = LocalArbitrageAnalyzer.GoldmineProfit;
    public const decimal GreatDealRoiPercent = LocalArbitrageAnalyzer.GoldmineRoiPercent;
    public const decimal CeilingProfit = LocalArbitrageAnalyzer.SolidProfit;
    public const decimal CeilingRoiPercent = LocalArbitrageAnalyzer.SolidRoiPercent;

    // ── The opening ladder ───────────────────────────────────────────────────────────────────
    // Where an opener starts before any signal moves it. Low enough to leave negotiating room,
    // high enough that the reply is a counter rather than silence.
    public const int BaseOpeningDiscountPercent = 15;
    // The hard limit on rudeness. Past roughly a third off, a stranger's offer stops reading as a
    // negotiation and starts reading as an insult — and an insult gets ignored, not countered, which
    // costs the whole deal rather than the difference. When the number that makes this work sits
    // below this floor, the plan says so instead of drafting a message that will not be answered.
    public const int MaxOpeningDiscountPercent = 35;
    // Without sold history there is no argument for a low number, only an assertion. A thin-evidence
    // opener stays shallow and the draft stops quoting figures.
    public const int ThinEvidenceCapPercent = 12;
    // Below this many sold comps the drafts don't put a price in front of a stranger.
    public const int MinCompsToCite = 3;

    // A listing nobody has bought is a listing the market has already priced. These are the two
    // points where that becomes an argument the seller can feel.
    public const int StaleListingDays = 14;
    public const int VeryStaleListingDays = 30;
    private const int StaleBonusPercent = 5;
    private const int VeryStaleBonusPercent = 8;
    // A seller who has already cut their own price has told you they want it gone.
    private const int PriceDroppedBonusPercent = 5;

    // On an already-good deal the ask is a courtesy, not a negotiation — see BuildBuyNow.
    private const int CourtesyTrimPercent = 5;

    /// <summary>
    /// The most that can be paid and still clear <paramref name="minProfit"/> in cash AND
    /// <paramref name="minRoiPercent"/> on the money — the stricter of the two, truncated rather
    /// than rounded, because this is a number someone negotiates against.
    /// </summary>
    public static decimal BuyPriceAt(decimal breakEvenBuyPrice, decimal minProfit, decimal minRoiPercent)
    {
        if (breakEvenBuyPrice <= 0) return 0m;

        var byProfit = breakEvenBuyPrice - minProfit;
        var byRoi = breakEvenBuyPrice / (1m + minRoiPercent / 100m);
        var price = Math.Min(byProfit, byRoi);
        return price <= 0 ? 0m : Math.Floor(price * 100m) / 100m;
    }

    /// <summary>What is left after every fee and cost if this is bought at <paramref name="price"/>.</summary>
    /// <remarks>
    /// Exact, not an approximation: net profit falls by exactly one dollar for every extra dollar
    /// paid locally, which is the same identity <c>LocalArbitrageOpportunity.MaxBuyPrice</c> is
    /// built on. So a whole ladder of counter-offers costs one subtraction each, and cannot drift
    /// away from the row it came from.
    /// </remarks>
    public static decimal NetAt(decimal breakEvenBuyPrice, decimal price) =>
        Math.Round(breakEvenBuyPrice - price, 2);

    public static decimal? RoiAt(decimal breakEvenBuyPrice, decimal price) =>
        price > 0 ? Math.Round(NetAt(breakEvenBuyPrice, price) / price * 100m, 1) : null;

    /// <summary>How good a buy this price is, on the same bars the arbitrage board judges by.</summary>
    public static string ToneAt(decimal breakEvenBuyPrice, decimal price)
    {
        var net = NetAt(breakEvenBuyPrice, price);
        if (net <= 0) return "loss";
        // A free item has no cost basis, so its return is unbounded rather than zero — the same rule
        // the ranking uses. Anything free that nets money is as good as a buy gets.
        var roi = RoiAt(breakEvenBuyPrice, price) ?? decimal.MaxValue;

        if (net >= GreatDealProfit && roi >= GreatDealRoiPercent) return "great";
        if (net >= CeilingProfit && roi >= CeilingRoiPercent) return "good";
        return "thin";
    }

    /// <summary>
    /// Offers land better on round numbers — "$340" reads as a considered figure and "$338.50" reads
    /// as a spreadsheet. Always rounds DOWN, so rounding can never push a price above a ceiling that
    /// was calculated to the cent.
    /// </summary>
    public static decimal RoundOffer(decimal price)
    {
        var step = Increment(price);
        return step <= 0 ? Math.Round(price, 2) : Math.Floor(price / step) * step;
    }

    private static decimal RoundOfferUp(decimal price)
    {
        var step = Increment(price);
        return step <= 0 ? Math.Round(price, 2) : Math.Ceiling(price / step) * step;
    }

    private static decimal Increment(decimal price) => price switch
    {
        >= 1000m => 25m,
        >= 300m => 10m,
        >= 100m => 5m,
        >= 10m => 1m,
        _ => 0m,
    };

    /// <summary>
    /// The whole plan for one local deal: the numbers, the reasoning, the counter-offer ladder and
    /// the drafts.
    /// </summary>
    /// <param name="askPrice">What the seller is asking.</param>
    /// <param name="breakEvenBuyPrice">
    /// Net proceeds after every fee and cost — the most that can be paid for zero profit. This is
    /// <c>LocalArbitrageOpportunity.MaxBuyPrice</c>, already costed by the shared profit calculator.
    /// </param>
    /// <param name="resalePrice">What the comps say it sells for; quoted in the draft only when
    /// <paramref name="compCount"/> justifies quoting anything.</param>
    /// <param name="daysListed">How long it has sat unsold, where the source publishes a date.</param>
    /// <param name="daysToCash">How long the money stays tied up once bought, per
    /// <see cref="DaysToCashEstimator"/> — the honest reason a low offer is a low offer.</param>
    public static NegotiationPlan Build(
        decimal askPrice, decimal breakEvenBuyPrice, decimal? resalePrice, int compCount,
        int? daysListed = null, int? daysToCash = null, decimal? originalPrice = null,
        double? distanceMiles = null)
    {
        var plan = new NegotiationPlan
        {
            AskPrice = Math.Round(Math.Max(0m, askPrice), 2),
            BreakEvenPrice = Math.Round(Math.Max(0m, breakEvenBuyPrice), 2),
            ResalePrice = resalePrice,
            CompCount = compCount,
            CitesComps = compCount >= MinCompsToCite && resalePrice is > 0m,
        };

        if (resalePrice is not > 0m || compCount <= 0)
        {
            plan.Verdict = "no_data";
            plan.Headline = "No sold history matched this one, so there is no honest number to negotiate against. " +
                            "Price it first — an offer with nothing behind it is just a guess with a dollar sign on it.";
            plan.EvidenceNote = "No matched sold comps.";
            return plan;
        }

        plan.EvidenceNote = plan.CitesComps
            ? $"{compCount} sold comp{Plural(compCount)} behind the {Cash(resalePrice.Value)} resale figure."
            : $"Only {compCount} sold comp{Plural(compCount)} — enough to rank this deal, not enough to quote a price at a stranger.";

        if (plan.BreakEvenPrice <= 0m)
        {
            plan.Verdict = "walk";
            plan.Headline = $"Fees and shipping eat the whole {Cash(resalePrice.Value)} sale price. " +
                            "There is no buy price that makes this work — not even free.";
            return plan;
        }

        var breakEven = plan.BreakEvenPrice;
        plan.TargetPrice = BuyPriceAt(breakEven, GreatDealProfit, GreatDealRoiPercent);
        var ceiling = BuyPriceAt(breakEven, CeilingProfit, CeilingRoiPercent);
        plan.NetAtAsk = NetAt(breakEven, plan.AskPrice);

        // A free listing isn't a negotiation. Go and get it.
        if (plan.AskPrice <= 0m)
        {
            plan.Verdict = "buy_now";
            plan.CeilingPrice = ceiling > 0 ? ceiling : null;
            plan.OpeningOffer = 0m;
            plan.NetAtOpening = plan.NetAtAsk;
            plan.Headline = $"It's free, and it clears {Cash(breakEven)} after fees. There is nothing to negotiate — " +
                            "be first, be polite, and turn up when you say you will.";
            plan.Messages.Add(new NegotiationMessage
            {
                Id = "claim", Label = "Claim it", When = "Now — free listings go to whoever answers first",
                Text = "Hi — is this still available? I can come pick it up today or whenever suits you, " +
                       "and I'll turn up when I say I will. Thanks either way!",
            });
            BuildLadder(plan, breakEven, ceiling);
            return plan;
        }

        // ── The opening ladder ───────────────────────────────────────────────────────────────
        var signals = new List<string>();
        var discount = BaseOpeningDiscountPercent;

        if (daysListed is int days && days >= StaleListingDays)
        {
            var bonus = days >= VeryStaleListingDays ? VeryStaleBonusPercent : StaleBonusPercent;
            discount += bonus;
            signals.Add($"Listed {days} days and still here — the market has already told this seller no at {Cash(plan.AskPrice)}.");
        }

        if (originalPrice is > 0m && originalPrice > plan.AskPrice)
        {
            discount += PriceDroppedBonusPercent;
            signals.Add($"They have already cut it from {Cash(originalPrice.Value)} — a seller who moves once will usually move again.");
        }

        // Asking more than the thing actually sells for. The opener has to at least close that gap,
        // or it is a discount off a price that was never real.
        if (plan.CitesComps && plan.AskPrice > resalePrice.Value)
        {
            var toResale = (int)Math.Ceiling((plan.AskPrice - resalePrice.Value) / plan.AskPrice * 100m);
            if (toResale > discount)
            {
                discount = toResale;
                signals.Add($"They are asking {Cash(plan.AskPrice)} for something that sells for {Cash(resalePrice.Value)} — " +
                            "the opener is sized to close that gap before anything else.");
            }
        }

        if (!plan.CitesComps && discount > ThinEvidenceCapPercent)
        {
            discount = ThinEvidenceCapPercent;
            signals.Add($"Held at {ThinEvidenceCapPercent}% because the sold history is thin. " +
                        "The draft leads on cash and pickup instead of on a figure it can't back up.");
        }

        discount = Math.Clamp(discount, 0, MaxOpeningDiscountPercent);

        // Take the cheaper of "what the signals justify asking" and "what actually makes this a great
        // buy" — there is no reason to open above a number that already wins.
        var politeFloor = plan.AskPrice * (100m - MaxOpeningDiscountPercent) / 100m;
        var fromLadder = plan.AskPrice * (100m - discount) / 100m;
        var wanted = plan.TargetPrice > 0m ? Math.Min(fromLadder, plan.TargetPrice) : fromLadder;

        var opening = RoundOffer(wanted);
        if (opening < politeFloor)
        {
            opening = Math.Min(plan.AskPrice, RoundOfferUp(politeFloor));
            signals.Add($"Held at the {MaxOpeningDiscountPercent}%-off floor. Lower than this and a stranger stops " +
                        "countering and starts ignoring you — which costs the whole deal, not the difference.");
        }
        opening = Math.Min(opening, plan.AskPrice);

        plan.OpeningOffer = opening;
        plan.OpeningDiscountPercent = plan.AskPrice > 0
            ? (int)Math.Round((plan.AskPrice - opening) / plan.AskPrice * 100m, MidpointRounding.AwayFromZero)
            : 0;
        plan.NetAtOpening = NetAt(breakEven, opening);
        plan.Upside = Math.Round(plan.AskPrice - opening, 2);
        plan.CeilingPrice = ceiling > 0m ? Math.Min(ceiling, plan.AskPrice) : null;
        plan.Signals = signals;

        // ── The verdict ──────────────────────────────────────────────────────────────────────
        // Read off where the ask sits against the two bars, and — when the ask is above both —
        // whether a polite offer can even reach them.
        var waitPhrase = WaitPhrase(daysToCash);
        if (plan.AskPrice <= plan.TargetPrice)
            BuildBuyNow(plan, breakEven, resalePrice.Value, distanceMiles);
        else if (plan.AskPrice <= ceiling)
            BuildNegotiate(plan, breakEven, resalePrice.Value, ceiling, daysListed, distanceMiles, waitPhrase, "negotiate");
        else if (opening <= ceiling)
            BuildNegotiate(plan, breakEven, resalePrice.Value, ceiling, daysListed, distanceMiles, waitPhrase, "must_negotiate");
        else if (opening < breakEven)
            BuildLongShot(plan, breakEven, resalePrice.Value, ceiling, daysListed, distanceMiles, waitPhrase);
        else
            BuildWalk(plan, breakEven);

        BuildLadder(plan, breakEven, ceiling);
        return plan;
    }

    // Already at or under the great-buy price. The risk here is not paying too much, it is losing
    // the deal to the next person over ten dollars — so the ask and the acceptance go in the same
    // message. That makes the ask free: there is no version of this where they walk away offended.
    private static void BuildBuyNow(
        NegotiationPlan plan, decimal breakEven, decimal resale, double? distanceMiles)
    {
        var trim = Math.Min(plan.OpeningOffer ?? plan.AskPrice,
                            RoundOffer(plan.AskPrice * (100m - CourtesyTrimPercent) / 100m));
        if (trim <= 0m || trim >= plan.AskPrice) trim = plan.AskPrice;

        plan.OpeningOffer = trim;
        plan.OpeningDiscountPercent = plan.AskPrice > 0
            ? (int)Math.Round((plan.AskPrice - trim) / plan.AskPrice * 100m, MidpointRounding.AwayFromZero) : 0;
        plan.NetAtOpening = NetAt(breakEven, trim);
        plan.Upside = Math.Round(plan.AskPrice - trim, 2);
        plan.Verdict = "buy_now";
        plan.CeilingPrice = plan.AskPrice;

        plan.Headline =
            $"Already a great buy at the {Cash(plan.AskPrice)} ask — {Cash(plan.NetAtAsk ?? 0m)} net after fees. " +
            (trim < plan.AskPrice
                ? $"Ask for {Cash(trim)}, but say yes either way in the same message. Don't lose this over {Cash(plan.Upside)}."
                : "Don't haggle. Be first, be easy to deal with, and go get it.");

        plan.Signals.Insert(0, $"The ask is already under your {Cash(plan.TargetPrice)} great-buy price. " +
                              "The risk on this one is losing it, not overpaying for it.");

        var pickup = PickupPhrase(distanceMiles);
        plan.Messages.Add(new NegotiationMessage
        {
            Id = "take_it",
            Label = trim < plan.AskPrice ? "Ask once, and take it either way" : "Take it",
            When = "Send now — this one is priced to go",
            Text = trim < plan.AskPrice
                ? $"Hi — is this still available?\n\n" +
                  $"I'd like to take it. Any chance you'd do {Cash(trim)} cash, picked up? If not, no problem at all — " +
                  $"I'll take it at your {Cash(plan.AskPrice)} asking price either way.\n\n" +
                  $"{pickup}Just let me know what time suits you. Thanks!"
                : $"Hi — is this still available?\n\n" +
                  $"I'll take it at your {Cash(plan.AskPrice)} asking price. {pickup}Just tell me when and where works and I'll be there.\n\n" +
                  "Thanks!",
        });
    }

    // The main case: open low with a reason, concede once, then stop at the ceiling.
    private static void BuildNegotiate(
        NegotiationPlan plan, decimal breakEven, decimal resale, decimal ceiling,
        int? daysListed, double? distanceMiles, string waitPhrase, string verdict)
    {
        plan.Verdict = verdict;
        var opening = plan.OpeningOffer!.Value;
        // The ceiling is calculated to the cent; the number said out loud is rounded DOWN off it,
        // because "$230.76 is my limit" sounds like a spreadsheet talking and invites the other side
        // to test whether the limit is real. Never below the opener, which would be conceding
        // backwards.
        var stop = Math.Max(opening, RoundOffer(Math.Min(ceiling, plan.AskPrice)));
        // The rounded number is THE ceiling from here on, including on the ladder. Showing $159.84
        // in the table beside a draft that says $155 is two limits on one screen, and the seller
        // then has to work out which one is real.
        plan.CeilingPrice = stop;

        plan.Headline = verdict == "negotiate"
            // The ceiling here is their own ask: this deal already works, so the honest instruction
            // is "open low, but pay their price rather than lose it" — not "stop at $180" when $180
            // is the number written on the listing.
            ? $"Worth doing at the {Cash(plan.AskPrice)} ask ({Cash(plan.NetAtAsk ?? 0m)} net), so everything you talk off is free money. " +
              $"Open at {Cash(opening)}" +
              (stop >= plan.AskPrice
                  ? " — and if they won't move, their price is still worth paying."
                  : $"; stop at {Cash(stop)}.")
            : $"Not worth {Cash(plan.AskPrice)}. Open at {Cash(opening)} and stop at {Cash(stop)} — " +
              $"above that the drive and the wait aren't paid for.";

        plan.Messages.Add(new NegotiationMessage
        {
            Id = "opening", Label = "Send this first", When = "First contact",
            Text = OpeningText(plan, resale, opening, daysListed, distanceMiles, waitPhrase),
        });

        // Concede once, halfway, rather than jumping to the ceiling. Going straight to your maximum
        // teaches the other side that your numbers move when pushed, and there is nothing left to
        // give when they push again.
        var middle = RoundOffer((opening + stop) / 2m);
        if (middle > opening && middle < stop)
        {
            plan.Messages.Add(new NegotiationMessage
            {
                Id = "counter", Label = "If they counter", When = "They said no, or named a higher number",
                Text = $"Thanks for getting back to me — I appreciate you considering it.\n\n" +
                       $"I can come up to {Cash(middle)}. That's cash, picked up, and I'll work around your schedule.\n\n" +
                       "If that works, just say the word and I'll be there.",
            });
        }

        // When the ceiling is their own asking price the last move is not a refusal, it's a yes.
        // Telling someone "$180 is as far as I can go" about their own $180 listing loses a deal
        // that was already worth doing, for nothing.
        plan.Messages.Add(stop >= plan.AskPrice
            ? new NegotiationMessage
            {
                Id = "final", Label = "Your last number", When = "They won't move — close it rather than lose it",
                Text = $"Fair enough — I'll take it at your {Cash(plan.AskPrice)} asking price then.\n\n" +
                       "I can come with cash whenever suits you. Just name a time and I'll be there.",
            }
            : new NegotiationMessage
            {
                Id = "final", Label = "Your last number", When = "They pushed again — do not go past this",
                Text = $"I understand — {Cash(stop)} is genuinely as far as I can go on this one, so I'll leave it with you.\n\n" +
                       "The offer stands if you change your mind, and if it's still around in a week or two I'm happy to " +
                       "come and get it same day. Good luck with the sale either way!",
            });
    }

    // A polite offer only reaches thin territory. Worth a message, but the plan says plainly that
    // this needs the seller to move a long way, so nobody talks themselves into it.
    private static void BuildLongShot(
        NegotiationPlan plan, decimal breakEven, decimal resale, decimal ceiling,
        int? daysListed, double? distanceMiles, string waitPhrase)
    {
        plan.Verdict = "long_shot";
        var opening = plan.OpeningOffer!.Value;
        ceiling = RoundOffer(ceiling);
        plan.CeilingPrice = ceiling;

        plan.Headline =
            $"Long shot. You'd need this under {Cash(ceiling)} for it to be worth the drive, and the lowest you can " +
            $"politely offer on a {Cash(plan.AskPrice)} ask is {Cash(opening)} — which only leaves {Cash(NetAt(breakEven, opening))}. " +
            "Send it if you like, but don't chase it.";

        plan.Signals.Add($"Break-even is {Cash(breakEven)}. Anything at or above that price is a favour to the seller, not a flip.");

        plan.Messages.Add(new NegotiationMessage
        {
            Id = "opening", Label = "Worth one message", When = "Once — then let it go",
            Text = OpeningText(plan, resale, opening, daysListed, distanceMiles, waitPhrase),
        });
    }

    // No polite number works. Say so and draft nothing — a message you shouldn't send is worse than
    // no message, because sending it is how a bad deal gets talked into.
    private static void BuildWalk(NegotiationPlan plan, decimal breakEven)
    {
        plan.Verdict = "walk";
        plan.OpeningOffer = null;
        plan.Upside = 0m;
        plan.NetAtOpening = null;
        plan.Headline =
            $"Walk. Even {MaxOpeningDiscountPercent}% off the {Cash(plan.AskPrice)} ask is above your {Cash(breakEven)} " +
            "break-even, so there is no offer here that both makes money and gets answered.";
        plan.Signals.Add($"You would have to pay under {Cash(breakEven)} just to make nothing. Leave it.");
    }

    // The draft itself. The persuasive part is not the number — it is the reason for the number, and
    // the reason is true: fees, shipping and the wait are real, and they are why the buy price has to
    // be what it is. Where the sold history is too thin to quote, it quotes nothing and leads on the
    // one thing a local seller genuinely values: someone who turns up with cash.
    private static string OpeningText(
        NegotiationPlan plan, decimal resale, decimal opening, int? daysListed,
        double? distanceMiles, string waitPhrase)
    {
        var pickup = PickupPhrase(distanceMiles);
        var stale = daysListed is int d && d >= StaleListingDays
            ? "I saw it's been up a little while — if you'd just like it gone, I can come today. "
            : "";

        if (!plan.CitesComps)
        {
            return "Hi — is this still available?\n\n" +
                   $"I'm interested if it is. I could do {Cash(opening)} cash, picked up. I know that's under your " +
                   $"{Cash(plan.AskPrice)} asking price — that's honestly just where my budget lands on this one.\n\n" +
                   $"{stale}{pickup}No worries at all if it's not for you. Thanks either way!";
        }

        return "Hi — is this still available?\n\n" +
               $"I've been looking at these for a while. Similar ones sell for around {Cash(resale)}, but by the time " +
               $"fees and shipping come out that's roughly {Cash(plan.BreakEvenPrice)} in hand" +
               (waitPhrase.Length > 0 ? $", and it's {waitPhrase} before that money actually turns up" : "") +
               ". So I have to be careful about what I pay up front.\n\n" +
               $"Would you take {Cash(opening)}? That's cash, picked up, no messing you around.\n\n" +
               $"{stale}{pickup}Completely understand if that doesn't work for you — thanks for your time either way!";
    }

    // Every rung a conversation might land on, and what each one actually leaves. This is the part
    // that gets read standing in a driveway with a phone in one hand: they name a number, and the
    // answer to "can I say yes to that?" has to be a colour, not a calculation.
    private static void BuildLadder(NegotiationPlan plan, decimal breakEven, decimal ceiling)
    {
        var rungs = new List<NegotiationRung>();

        void Add(string label, decimal? price, bool opening = false, bool ceilingRung = false,
                 bool ask = false, bool breakEvenRung = false)
        {
            if (price is not decimal p || p < 0m) return;
            // A price already on the ladder keeps its first (more specific) label rather than
            // appearing twice — the opener and the target are routinely the same number.
            var existing = rungs.FirstOrDefault(r => r.Price == p);
            if (existing is not null)
            {
                existing.IsOpening |= opening;
                existing.IsCeiling |= ceilingRung;
                existing.IsAsk |= ask;
                existing.IsBreakEven |= breakEvenRung;
                return;
            }

            rungs.Add(new NegotiationRung
            {
                Label = label, Price = p,
                NetProfit = NetAt(breakEven, p), RoiPercent = RoiAt(breakEven, p),
                Tone = ToneAt(breakEven, p),
                IsOpening = opening, IsCeiling = ceilingRung, IsAsk = ask, IsBreakEven = breakEvenRung,
            });
        }

        Add("Your opening offer", plan.OpeningOffer, opening: true);
        if (plan.TargetPrice > 0m) Add("A great buy", plan.TargetPrice);
        Add("Your ceiling — stop here", plan.CeilingPrice, ceilingRung: true);
        Add("Their asking price", plan.AskPrice, ask: true);
        Add("Break-even — you make nothing", breakEven, breakEvenRung: true);

        plan.Ladder = [.. rungs.OrderBy(r => r.Price)];
    }

    // The wait, in the way a person would say it. Short waits are left out of the draft entirely —
    // "it takes about twelve days" is not an argument for a lower price, it's a detail.
    private static string WaitPhrase(int? daysToCash) => daysToCash switch
    {
        null or < 21 => "",
        < 45 => "a month or so",
        < 105 => $"{Math.Round(daysToCash!.Value / 30m, MidpointRounding.AwayFromZero):0} months or so",
        _ => "the better part of a year",
    };

    // The one thing a local seller actually values that money can't buy: certainty that you'll show
    // up. Distance is mentioned only when it's close enough to be reassuring.
    private static string PickupPhrase(double? distanceMiles) => distanceMiles switch
    {
        null => "",
        <= 30 => $"I'm only about {Math.Round(distanceMiles.Value)} miles away, so pickup is easy. ",
        _ => "I'm happy to make the drive. ",
    };

    private static string Plural(int n) => n == 1 ? "" : "s";

    // Whole dollars read as a considered offer; cents read as a spreadsheet. Both are used — the
    // ladder prices are exact and the drafted offers are round.
    private static string Cash(decimal value) =>
        value == Math.Floor(value)
            ? value.ToString("$#,##0", CultureInfo.InvariantCulture)
            : value.ToString("$#,##0.00", CultureInfo.InvariantCulture);
}
