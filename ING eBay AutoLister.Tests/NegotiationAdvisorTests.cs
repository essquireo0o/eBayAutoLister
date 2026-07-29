using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The buy side is the cheap side — a dollar talked off the ask is a dollar of profit with no fee,
// no shipping and no wait — so what's pinned here is the discipline that makes the advice worth
// following: the ceiling is a real ceiling, an offer is never so low it gets ignored instead of
// countered, a draft never quotes a resale figure the sold history can't support, and a deal that
// can't be made produces no message at all.
public class NegotiationAdvisorTests
{
    // A $400 resale that nets $300 after every fee: pay under $300 and there is money in it.
    private const decimal BreakEven = 300m;

    // ── The ceiling and the target ───────────────────────────────────────────────────────────

    [Fact]
    public void BuyPriceAt_TakesTheStricterOfTheCashBarAndTheReturnBar()
    {
        // At a $300 break-even, 75% ROI allows $171.42 and $75 cash allows $225 — ROI binds.
        Assert.Equal(171.42m, NegotiationAdvisor.BuyPriceAt(300m, 75m, 75m));

        // On a big-ticket item the flat cash bar is trivially cleared, so the return bar binds
        // again — the two only swap on cheap items.
        Assert.Equal(75m, NegotiationAdvisor.BuyPriceAt(150m, 75m, 100m));
    }

    [Fact]
    public void BuyPriceAt_TruncatesRatherThanRoundsUp()
    {
        // This is a number someone negotiates against: rounding it up would quietly authorise
        // paying a cent more than the bar allows.
        var price = NegotiationAdvisor.BuyPriceAt(1000m, 75m, 75m);
        Assert.Equal(571.42m, price);
        Assert.True(price * 1.75m <= 1000m);
    }

    [Fact]
    public void BuyPriceAt_NoBreakEvenMeansNoPrice()
    {
        Assert.Equal(0m, NegotiationAdvisor.BuyPriceAt(0m, 75m, 75m));
        Assert.Equal(0m, NegotiationAdvisor.BuyPriceAt(-40m, 75m, 75m));
        // A break-even smaller than the cash bar leaves nothing that clears it.
        Assert.Equal(0m, NegotiationAdvisor.BuyPriceAt(50m, 75m, 75m));
    }

    [Fact]
    public void NetAt_IsExactlyTheBreakEvenMinusThePrice()
    {
        // The identity the whole ladder rests on: net profit falls one dollar per extra dollar paid.
        Assert.Equal(120m, NegotiationAdvisor.NetAt(BreakEven, 180m));
        Assert.Equal(0m, NegotiationAdvisor.NetAt(BreakEven, BreakEven));
        Assert.Equal(-50m, NegotiationAdvisor.NetAt(BreakEven, 350m));
    }

    [Fact]
    public void ToneAt_UsesTheSameBarsTheArbitrageBoardJudgesBy()
    {
        // A $1,200 break-even, because the bars are stated in cash and a $300 one cannot reach
        // them: $400 buy leaves $800 at 200% — the goldmine bar on both axes.
        Assert.Equal("great", NegotiationAdvisor.ToneAt(1200m, 400m));
        // $920: $280 at 30.4% — clears the worth-doing bar, nowhere near the goldmine return.
        Assert.Equal("good", NegotiationAdvisor.ToneAt(1200m, 920m));
        // $1,160: $40. Real, and under the cash bar that makes a flip worth the work.
        Assert.Equal("thin", NegotiationAdvisor.ToneAt(1200m, 1160m));
        Assert.Equal("loss", NegotiationAdvisor.ToneAt(1200m, 1280m));
    }

    [Fact]
    public void ToneAt_FreeIsUnboundedReturn_NotZero()
    {
        // No cost basis means the return is undefined rather than 0% — the same rule the ranking
        // uses. Anything free that nets real money is as good as a buy gets.
        Assert.Equal("great", NegotiationAdvisor.ToneAt(BreakEven, 0m));
    }

    // ── Rounding ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RoundOffer_LandsOnRoundNumbersAndAlwaysRoundsDown()
    {
        Assert.Equal(340m, NegotiationAdvisor.RoundOffer(348.72m));
        Assert.Equal(145m, NegotiationAdvisor.RoundOffer(149.99m));
        Assert.Equal(42m, NegotiationAdvisor.RoundOffer(42.80m));
        Assert.Equal(1225m, NegotiationAdvisor.RoundOffer(1249m));
        // Rounding down can never push an offer above a ceiling calculated to the cent.
        Assert.True(NegotiationAdvisor.RoundOffer(171.42m) <= 171.42m);
    }

    [Fact]
    public void RoundOffer_LeavesSmallPricesAlone()
    {
        // A $1 increment on an $8 item is a 12% swing, which is a decision and not a rounding.
        Assert.Equal(8.50m, NegotiationAdvisor.RoundOffer(8.50m));
    }

    // ── The verdicts ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_NoSoldHistoryDraftsNothing()
    {
        var plan = NegotiationAdvisor.Build(askPrice: 200m, breakEvenBuyPrice: 0m, resalePrice: null, compCount: 0);

        Assert.Equal("no_data", plan.Verdict);
        Assert.Empty(plan.Messages);
        Assert.Null(plan.OpeningOffer);
    }

    [Fact]
    public void Build_AskAlreadyUnderTheGreatBuyPriceSaysTakeIt()
    {
        // Target on a $1,200 break-even is $685.71, and they want $480.
        var plan = NegotiationAdvisor.Build(
            askPrice: 480m, breakEvenBuyPrice: 1200m, resalePrice: 1600m, compCount: 8);

        Assert.Equal("buy_now", plan.Verdict);
        Assert.Equal(720m, plan.NetAtAsk);
        // The ceiling on a deal this good is their own ask — there is no case for paying more.
        Assert.Equal(480m, plan.CeilingPrice);
    }

    [Fact]
    public void Build_TheAskOnAGreatDealIsMadeRiskFreeInTheSameMessage()
    {
        var plan = NegotiationAdvisor.Build(
            askPrice: 480m, breakEvenBuyPrice: 1200m, resalePrice: 1600m, compCount: 8);

        var message = Assert.Single(plan.Messages).Text;
        // The whole point: the discount is asked for AND the asking price is accepted in the same
        // message, so there is no version of this where a $720 flip is lost over a haggle. Both
        // numbers are read off the plan rather than written in, so this pins the behaviour and not
        // the courtesy-trim arithmetic.
        Assert.NotNull(plan.OpeningOffer);
        Assert.True(plan.OpeningOffer < plan.AskPrice, "a great buy should still be asked down once");
        Assert.Contains($"${plan.OpeningOffer:0}", message);   // the cheeky ask
        Assert.Contains("$480", message);                      // and their own price, taken regardless
        Assert.Contains("either way", message);
    }

    [Fact]
    public void Build_ProfitableAtTheAskStillOpensLower()
    {
        // $880 ask on a $1,200 break-even: $320 net at 36% ROI, which clears the ceiling — so this
        // is already worth buying, and everything talked off is pure bonus.
        var plan = NegotiationAdvisor.Build(
            askPrice: 880m, breakEvenBuyPrice: 1200m, resalePrice: 1600m, compCount: 10);

        Assert.Equal("negotiate", plan.Verdict);
        Assert.NotNull(plan.OpeningOffer);
        Assert.True(plan.OpeningOffer < 880m);
        // Every dollar of the gap is profit with no fee and no wait attached.
        Assert.Equal(880m - plan.OpeningOffer!.Value, plan.Upside);
    }

    [Fact]
    public void Build_OpensAtTheGreatBuyPriceRatherThanAShallowerDiscountWhenThatIsCheaper()
    {
        // 15% off a $1,600 ask is $1,360; the price that makes this a great buy is $1,142.85. There
        // is no reason to open above a number that already wins, so the opener follows the target
        // down — as far as the politeness floor allows.
        var plan = NegotiationAdvisor.Build(
            askPrice: 1600m, breakEvenBuyPrice: 2000m, resalePrice: 2800m, compCount: 12);

        Assert.True(plan.OpeningOffer < 1360m,
            $"opened at {plan.OpeningOffer}, which is the shallow ladder discount rather than the cheaper target");
        Assert.True(plan.OpeningOffer <= plan.TargetPrice);
    }

    [Fact]
    public void Build_NeverOpensBelowThePolitenessFloor()
    {
        // The great-buy price here is $171.42 against a $400 ask — a 57% lowball. That does not get
        // countered, it gets ignored, which costs the whole deal rather than the difference.
        var plan = NegotiationAdvisor.Build(
            askPrice: 400m, breakEvenBuyPrice: BreakEven, resalePrice: 400m, compCount: 12);

        var floor = 400m * (100m - NegotiationAdvisor.MaxOpeningDiscountPercent) / 100m;
        Assert.NotNull(plan.OpeningOffer);
        Assert.True(plan.OpeningOffer >= floor,
            $"opened at {plan.OpeningOffer} which is below the {floor} politeness floor");
        Assert.Contains(plan.Signals, s => s.Contains("floor"));
    }

    [Fact]
    public void Build_WalksWhenNoPoliteOfferClearsTheFees()
    {
        // $500 ask against a $300 break-even. 35% off is $325 — still a loss.
        var plan = NegotiationAdvisor.Build(
            askPrice: 500m, breakEvenBuyPrice: BreakEven, resalePrice: 400m, compCount: 12);

        Assert.Equal("walk", plan.Verdict);
        // A message you shouldn't send is worse than no message: sending it is how a bad deal gets
        // talked into.
        Assert.Empty(plan.Messages);
        Assert.Null(plan.OpeningOffer);
        Assert.Equal(0m, plan.Upside);
    }

    [Fact]
    public void Build_NoBuyPriceWorksAtAllIsAWalk_NotANoData()
    {
        var plan = NegotiationAdvisor.Build(
            askPrice: 80m, breakEvenBuyPrice: 0m, resalePrice: 40m, compCount: 9);

        Assert.Equal("walk", plan.Verdict);
        Assert.Empty(plan.Messages);
    }

    [Fact]
    public void Build_FreeIsNotANegotiation()
    {
        var plan = NegotiationAdvisor.Build(
            askPrice: 0m, breakEvenBuyPrice: BreakEven, resalePrice: 400m, compCount: 9);

        Assert.Equal("buy_now", plan.Verdict);
        var message = Assert.Single(plan.Messages);
        Assert.Equal("claim", message.Id);
        // Nothing in a free listing is worth haggling over, so no number appears.
        Assert.DoesNotContain("$", message.Text);
    }

    // ── The leverage ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_AStaleListingJustifiesALowerOpener()
    {
        var fresh = NegotiationAdvisor.Build(
            askPrice: 250m, breakEvenBuyPrice: 400m, resalePrice: 520m, compCount: 9, daysListed: 2);
        var stale = NegotiationAdvisor.Build(
            askPrice: 250m, breakEvenBuyPrice: 400m, resalePrice: 520m, compCount: 9, daysListed: 45);

        Assert.True(stale.OpeningOffer <= fresh.OpeningOffer);
        Assert.Contains(stale.Signals, s => s.Contains("45 days"));
        // And it becomes a line in the draft — politely, as an offer to take it off their hands,
        // never as a dig about it not having sold.
        Assert.Contains("been up a little while", stale.Messages[0].Text);
        Assert.DoesNotContain("been up a little while", fresh.Messages[0].Text);
    }

    [Fact]
    public void Build_APriceTheSellerAlreadyCutIsLeverage_ButNotSomethingTheDraftPointsOut()
    {
        var plan = NegotiationAdvisor.Build(
            askPrice: 250m, breakEvenBuyPrice: 400m, resalePrice: 520m, compCount: 9,
            originalPrice: 320m);

        Assert.Contains(plan.Signals, s => s.Contains("$320"));
        // Telling a stranger you noticed them dropping their price is how a negotiation starts badly.
        Assert.DoesNotContain("320", plan.Messages[0].Text);
    }

    [Fact]
    public void Build_AnAskAboveWhatTheThingSellsForIsClosedByTheOpener()
    {
        // Asking $400 for something that sells for $300. A 15% opener is $340 — still above the
        // going rate, which would be a discount off a price that was never real — so the 25% gap
        // sets the discount instead.
        var plan = NegotiationAdvisor.Build(
            askPrice: 400m, breakEvenBuyPrice: 700m, resalePrice: 300m, compCount: 20);

        Assert.True(plan.OpeningOffer <= 300m,
            $"opened at {plan.OpeningOffer}, which is not sized to close the gap to the $300 going rate");
        Assert.Contains(plan.Signals, s => s.Contains("sells for"));
    }

    [Fact]
    public void Build_ThePolitenessFloorStillBindsOnAWildlyOverpricedAsk()
    {
        // Asking $500 for a $300 item is a 40% gap, and the floor caps the opener at 35%. The gap
        // does NOT get to override the one rule that keeps the message answerable — a number low
        // enough to be ignored costs the whole deal, not the difference.
        var plan = NegotiationAdvisor.Build(
            askPrice: 500m, breakEvenBuyPrice: 700m, resalePrice: 300m, compCount: 20);

        Assert.True(plan.OpeningOffer >= 500m * (100m - NegotiationAdvisor.MaxOpeningDiscountPercent) / 100m);
        Assert.True(plan.OpeningDiscountPercent <= NegotiationAdvisor.MaxOpeningDiscountPercent);
    }

    // ── Honesty ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_ThinSoldHistoryQuotesNoFigureAtAStranger()
    {
        var plan = NegotiationAdvisor.Build(
            askPrice: 250m, breakEvenBuyPrice: 400m, resalePrice: 520m, compCount: 2);

        Assert.False(plan.CitesComps);
        var text = plan.Messages[0].Text;
        Assert.DoesNotContain("520", text);
        Assert.DoesNotContain("Similar ones sell", text);
        // It leads on the thing that is true regardless of the comp count.
        Assert.Contains("budget", text);
    }

    [Fact]
    public void Build_ThinSoldHistoryAlsoCapsHowLowTheOpenerGoes()
    {
        var thin = NegotiationAdvisor.Build(
            askPrice: 1000m, breakEvenBuyPrice: 1600m, resalePrice: 2080m, compCount: 2, daysListed: 60);
        var solid = NegotiationAdvisor.Build(
            askPrice: 1000m, breakEvenBuyPrice: 1600m, resalePrice: 2080m, compCount: 20, daysListed: 60);

        // Without evidence there is no argument for a low number, only an assertion.
        Assert.True(thin.OpeningOffer > solid.OpeningOffer);
        Assert.True(thin.OpeningDiscountPercent <= NegotiationAdvisor.ThinEvidenceCapPercent);
    }

    [Fact]
    public void Build_TheDraftExplainsTheOfferWithTheRealReason()
    {
        var plan = NegotiationAdvisor.Build(
            askPrice: 380m, breakEvenBuyPrice: BreakEven, resalePrice: 400m, compCount: 14,
            daysToCash: 70);

        var text = plan.Messages[0].Text;
        Assert.Contains("$400", text);          // what they sell for
        Assert.Contains("$300", text);          // what is left after fees and shipping
        Assert.Contains("months or so", text);  // and how long before that money turns up
        Assert.Contains("fees and shipping", text);
    }

    [Fact]
    public void Build_AShortWaitIsNotUsedAsAnArgument()
    {
        // "It takes about twelve days" is a detail, not a reason to pay less, and padding the draft
        // with it makes the parts that are arguments read like padding too.
        var plan = NegotiationAdvisor.Build(
            askPrice: 380m, breakEvenBuyPrice: BreakEven, resalePrice: 400m, compCount: 14,
            daysToCash: 12);

        Assert.DoesNotContain("before that money", plan.Messages[0].Text);
    }

    [Fact]
    public void Build_TheDraftNeverInventsUrgencyOrRunsTheItemDown()
    {
        var plan = NegotiationAdvisor.Build(
            askPrice: 380m, breakEvenBuyPrice: BreakEven, resalePrice: 400m, compCount: 14,
            daysListed: 40, originalPrice: 450m);

        foreach (var message in plan.Messages)
        {
            foreach (var banned in new[] { "today only", "right now or", "worn out", "beat up", "nobody wants" })
                Assert.DoesNotContain(banned, message.Text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── The sequence ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_ConcedesOnceInTheMiddleRatherThanJumpingToTheCeiling()
    {
        // $1,200 ask against a $1,200 break-even: the ceiling is $923, and a 35%-off opener ($780)
        // reaches it — so this is a deal that can be made, and gets the whole sequence.
        var plan = NegotiationAdvisor.Build(
            askPrice: 1200m, breakEvenBuyPrice: 1200m, resalePrice: 1600m, compCount: 14);

        Assert.Equal("must_negotiate", plan.Verdict);
        Assert.Equal(["opening", "counter", "final"], plan.Messages.Select(m => m.Id));

        // Going straight to your maximum teaches the other side that your numbers move when pushed,
        // and leaves nothing to give when they push again. The middle offer is a real concession
        // above the opener and still short of the ceiling.
        Assert.DoesNotContain($"${plan.CeilingPrice:0}", plan.Messages[1].Text);
        Assert.True(plan.OpeningOffer < plan.CeilingPrice);
    }

    [Fact]
    public void Build_TheLastMessageQuotesTheCeilingAndNothingAboveIt()
    {
        var plan = NegotiationAdvisor.Build(
            askPrice: 1200m, breakEvenBuyPrice: 1200m, resalePrice: 1600m, compCount: 14);

        var final = plan.Messages.Single(m => m.Id == "final");
        // Said out loud as a round number, and rounded DOWN off the ceiling — never up. The same
        // number everywhere: a ladder showing one limit next to a draft saying another is two
        // limits on one screen.
        Assert.Contains($"${plan.CeilingPrice:0}", final.Text);
        Assert.Contains(plan.Ladder, r => r.IsCeiling && r.Price == plan.CeilingPrice);
        // Break-even never appears as a number anyone is invited to pay.
        Assert.DoesNotContain("$1200", final.Text);
    }

    [Fact]
    public void Build_TheCeilingIsNeverAboveTheirAsk()
    {
        // A ceiling above the asking price would invite paying more than they asked for.
        var plan = NegotiationAdvisor.Build(
            askPrice: 180m, breakEvenBuyPrice: 600m, resalePrice: 800m, compCount: 14);

        Assert.True(plan.CeilingPrice <= 180m);
    }

    // ── The ladder ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_TheLadderIsOrderedAndCarriesTheNumbersThatMatter()
    {
        var plan = NegotiationAdvisor.Build(
            askPrice: 280m, breakEvenBuyPrice: BreakEven, resalePrice: 400m, compCount: 14);

        Assert.NotEmpty(plan.Ladder);
        var prices = plan.Ladder.Select(r => r.Price).ToList();
        Assert.Equal(prices.OrderBy(p => p), prices);
        Assert.Contains(plan.Ladder, r => r.IsOpening);
        Assert.Contains(plan.Ladder, r => r.IsAsk);
        Assert.Contains(plan.Ladder, r => r.IsBreakEven);

        // Every rung agrees with the identity the row itself is built on.
        foreach (var rung in plan.Ladder)
            Assert.Equal(BreakEven - rung.Price, rung.NetProfit);
    }

    [Fact]
    public void Build_TheLadderNeverListsTheSamePriceTwice()
    {
        // The opener and the great-buy price are routinely the same number.
        var plan = NegotiationAdvisor.Build(
            askPrice: 200m, breakEvenBuyPrice: BreakEven, resalePrice: 400m, compCount: 14);

        Assert.Equal(plan.Ladder.Select(r => r.Price).Distinct().Count(), plan.Ladder.Count);
    }

    [Fact]
    public void Build_TheBreakEvenRungIsMarkedAsALoss()
    {
        var plan = NegotiationAdvisor.Build(
            askPrice: 280m, breakEvenBuyPrice: BreakEven, resalePrice: 400m, compCount: 14);

        var breakEvenRung = plan.Ladder.Single(r => r.IsBreakEven);
        Assert.Equal(0m, breakEvenRung.NetProfit);
        Assert.Equal("loss", breakEvenRung.Tone);
    }
}
