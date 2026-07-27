using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// Snap &amp; Source turns one priced row into one word. These pin the two things that word has to
/// be right about: that it never disagrees with the board the row came from, and that the no-price
/// case — the yard-sale case, where nobody has said a number yet — answers with a price to pay
/// instead of a profit figure measured against a zero the seller was never offered.
/// </summary>
public class SnapJudgeTests
{
    private static readonly LocalArbitrageAnalyzer Analyzer =
        new(new ProfitCalculator(), new LiquidationLotPricer(new ProfitCalculator()));
    private static readonly FeeProfile Fees = new(); // 13.25% + $0.40, no promoted/shipping/labor

    private static LocalSupplyListing Listing(decimal price, string title = "Bitmain Antminer S19j Pro") =>
        new()
        {
            Source = "snap", SourceLabel = "Craigslist", ItemId = "snap",
            Title = title, Url = "https://sfbay.craigslist.org/x/7712345678.html",
            Price = price,
        };

    private static ResalePricing Pricing(
        decimal? expected = 200m, int soldComps = 8, int confidence = 70,
        int? pricedComps = null, bool identityVerified = true) =>
        new()
        {
            LookupTitle = "Bitmain Antminer S19j Pro 104TH",
            Median = expected, ExpectedSale = expected, QuickSale = expected * 0.85m,
            SoldCompCount = soldComps, PricedCompCount = pricedComps ?? soldComps,
            IdentityVerified = identityVerified,
            ConfidenceScore = confidence, ConfidenceLevel = "Good",
        };

    private static SnapResult Snap(decimal? ask, ResalePricing? pricing = null, string title = "Bitmain Antminer S19j Pro")
    {
        var row = Analyzer.Build(Listing(ask ?? 0m, title), pricing ?? Pricing(), Fees);
        return SnapJudge.Build(row, askWasKnown: ask is > 0m);
    }

    // ── PayUpTo: the number the seller actually acts on ──────────────────────

    [Fact]
    public void PayUpTo_SatisfiesBothHalvesOfTheWorthDoingBar()
    {
        // At a $173.10 break-even: the ROI bar allows 173.10 / 1.30 = $133.15, the dollar bar allows
        // 173.10 - 25 = $148.10. The tighter of the two is the answer, because a price that clears
        // one bar and fails the other is not a price the app calls worth doing.
        var payUpTo = SnapJudge.PayUpTo(173.10m);

        Assert.Equal(133.15m, payUpTo);

        var profit = 173.10m - payUpTo;
        Assert.True(profit >= LocalArbitrageAnalyzer.SolidProfit);
        Assert.True(profit / payUpTo * 100m >= LocalArbitrageAnalyzer.SolidRoiPercent);
    }

    [Fact]
    public void PayUpTo_IsFlooredSoTheNumberQuotedIsSafeAtTheNumberQuoted()
    {
        // Rounding up would name a price that fails the bar it was derived from — by a cent, but
        // the whole value of this figure is that it can be handed over without arithmetic.
        var payUpTo = SnapJudge.PayUpTo(100m);
        Assert.Equal(75m, payUpTo);
        Assert.True((100m - payUpTo) / payUpTo * 100m >= LocalArbitrageAnalyzer.SolidRoiPercent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-40)]
    [InlineData(20)]   // clears the ROI bar but never the $25 one
    public void PayUpTo_IsZeroWhenNoPriceCouldClearTheBar(decimal breakEven) =>
        Assert.Equal(0m, SnapJudge.PayUpTo(breakEven));

    // ── With a price on it ───────────────────────────────────────────────────

    [Fact]
    public void AGoodDealAtAKnownPriceIsABuy()
    {
        var snap = Snap(50m);

        Assert.Equal(SnapCalls.Buy, snap.Call);
        Assert.Equal("BUY", snap.CallLabel);
        Assert.True(snap.AskWasKnown);
        Assert.Equal(50m, snap.AskPrice);
        Assert.Equal(123.10m, snap.NetProfit);
        Assert.Equal(173.10m, snap.BuyMax);
        Assert.Equal(133.15m, snap.PayUpTo);
        Assert.Equal(39.95m, snap.ProfitAtPayUpTo);   // 173.10 break-even - 133.15 paid
    }

    [Fact]
    public void ALossAtTheAskIsAPass()
    {
        var snap = Snap(190m);

        Assert.Equal(SnapCalls.Pass, snap.Call);
        Assert.Equal("PASS", snap.CallLabel);
        Assert.True(snap.NetProfit < 0m);
        // Still told what it WOULD be worth. "Pass" plus a number is a counter-offer; "pass" alone
        // is the end of the conversation.
        Assert.Equal(133.15m, snap.PayUpTo);
    }

    [Fact]
    public void AThinMarginIsACloseCallRatherThanRoundedEitherWay()
    {
        // $10 net on a $150 buy: real money, nowhere near the bar. Calling it BUY sends the seller
        // to spend $150 for a Saturday's work; calling it PASS throws away a decision they might
        // legitimately make on a fast mover.
        var snap = Snap(150m);

        Assert.Equal(SnapCalls.Close, snap.Call);
        Assert.Equal("CLOSE CALL", snap.CallLabel);
        Assert.True(snap.NetProfit > 0m);
    }

    [Fact]
    public void TheVerdictAndTheSentenceComeStraightFromTheBoardsOwnRow()
    {
        // The whole design: no second opinion. A snap and a Local Deals row for the same item at
        // the same price must be the same judgement, in the same words.
        var row = Analyzer.Build(Listing(50m), Pricing(), Fees);
        var snap = SnapJudge.Build(row, askWasKnown: true);

        Assert.Equal(row.VerdictNote, snap.Reason);
        Assert.Equal(row.NetProfit, snap.NetProfit);
        Assert.Equal(row.RoiPercent, snap.RoiPercent);
        Assert.Equal(row.EvidenceTier, snap.EvidenceTier);
    }

    // ── With no price named — the yard-sale case ─────────────────────────────

    [Fact]
    public void NoPriceNamedAnswersWithThePriceToStopAt()
    {
        var snap = Snap(null);

        Assert.Equal(SnapCalls.BuyUnder, snap.Call);
        Assert.StartsWith("BUY UNDER", snap.CallLabel);
        Assert.Contains("133", snap.CallLabel);
        Assert.Equal(133.15m, snap.PayUpTo);
        Assert.Equal(173.10m, snap.BuyMax);
        Assert.Contains("Pay up to", snap.Reason);
    }

    [Fact]
    public void NoPriceNamedNeverPublishesAProfitMeasuredAgainstZero()
    {
        // The row underneath WAS costed against a zero — that is what makes MaxBuyPrice come back
        // as the break-even. Publishing that arithmetic as "your profit" would be the most
        // flattering lie this screen could tell: $173 of profit on a price nobody has offered.
        var row = Analyzer.Build(Listing(0m), Pricing(), Fees);
        Assert.Equal(173.10m, row.NetProfit);

        var snap = SnapJudge.Build(row, askWasKnown: false);

        Assert.False(snap.AskWasKnown);
        Assert.Null(snap.AskPrice);
        Assert.Null(snap.NetProfit);
        Assert.Null(snap.RoiPercent);
        // The resale price is not arithmetic about a cost basis, so it survives.
        Assert.Equal(200m, snap.ResalePrice);
    }

    [Fact]
    public void SomethingNotWorthCollectingIsAPassEvenWithNoPriceOnIt()
    {
        // $12 resale leaves about $10 after eBay's cut. Free is not a reason to spend an evening
        // photographing, listing and packing it, and the ROI on a zero cost basis is unbounded —
        // which is exactly how this row would otherwise be crowned.
        var snap = Snap(null, Pricing(expected: 12m));

        Assert.Equal(SnapCalls.Pass, snap.Call);
        Assert.Equal("PASS", snap.CallLabel);
        Assert.Contains("even if they hand it to you", snap.Reason);
    }

    [Fact]
    public void SomethingWorthlessAtAnyPriceSaysSoInPlainWords()
    {
        var snap = Snap(null, Pricing(expected: 0.40m));

        Assert.Equal(SnapCalls.Pass, snap.Call);
        Assert.Contains("Even free", snap.Reason);
    }

    // ── When nothing priced it ───────────────────────────────────────────────

    [Fact]
    public void NoCompsMeansCantPriceIt_NotPass()
    {
        // "Pass" would be a judgement the app has not earned: nothing was compared to anything. The
        // seller's next move is to look at the sold listings, not to walk away.
        var row = Analyzer.Build(Listing(50m), resale: null, Fees);
        var snap = SnapJudge.Build(row, askWasKnown: true);

        Assert.Equal(SnapCalls.Unknown, snap.Call);
        Assert.Equal("CAN'T PRICE IT", snap.CallLabel);
        Assert.Null(snap.PayUpTo);
        Assert.Null(snap.BuyMax);
        Assert.NotEqual("", snap.Reason);
    }

    [Fact]
    public void TheReasonForNotPricingItIsTheOneTheValuationGave()
    {
        // "No sold history matched this title" and "the comps for this truck are tow hitches" are
        // different problems with different next steps, and this row is where the seller finds out
        // which one they have.
        var row = Analyzer.Build(Listing(50m), resale: null, Fees);
        var snap = SnapJudge.Build(row, askWasKnown: true);

        Assert.Equal(row.VerdictNote, snap.Reason);
    }

    // ── Evidence and warnings ────────────────────────────────────────────────

    [Fact]
    public void AnUnverifiedIdentityCannotReachABuy()
    {
        // Priced off another product entirely. No comp count rescues that, so the call is capped
        // and the evidence line says why in the analyzer's own words.
        var snap = Snap(50m, Pricing(identityVerified: false));

        Assert.False(snap.IdentityVerified);
        Assert.NotEqual(SnapCalls.Buy, snap.Call);
        Assert.Equal(LocalArbitrageEvidence.Low, snap.EvidenceTier);
        Assert.Contains("model or part number", snap.EvidenceNote);
    }

    [Fact]
    public void ThinEvidenceCarriesTheAnalyzersOwnSentence()
    {
        var snap = Snap(50m, Pricing(soldComps: 2, pricedComps: 2));

        Assert.Equal(LocalArbitrageEvidence.Low, snap.EvidenceTier);
        Assert.Contains("2 sold comps", snap.EvidenceNote);
    }

    [Fact]
    public void TheEvidenceSentenceIsNeverAlsoAWarning()
    {
        // A browser pass caught this rendering twice on one card — once in the evidence strip, once
        // as a bullet under it. A caveat repeated is a caveat discounted.
        foreach (var snap in new[]
                 {
                     Snap(50m),
                     Snap(50m, Pricing(soldComps: 2, pricedComps: 2)),
                     Snap(50m, Pricing(identityVerified: false)),
                 })
        {
            Assert.DoesNotContain(snap.Warnings, w => w == snap.EvidenceNote);
        }
    }

    [Fact]
    public void TheVerdictAloneRaisesNoWarnings()
    {
        // Warnings is for what the evidence line cannot say — what a page failed to publish, and
        // what a photo cannot tell you. A judged row on its own contributes none.
        Assert.Empty(Snap(50m).Warnings);
        Assert.Empty(Snap(50m, Pricing(soldComps: 2, pricedComps: 2)).Warnings);
    }

    [Fact]
    public void CompCountIsTheCompsThatPricedItNotTheCompsTheLookupReturned()
    {
        // Twelve comps found and one used is how a one-sale valuation earns a badge that says
        // twelve. The screen quotes the number the verdict actually rests on.
        var snap = Snap(50m, Pricing(soldComps: 12, pricedComps: 1));
        Assert.Equal(1, snap.CompCount);
    }

    // ── The photo's own caveats ──────────────────────────────────────────────

    [Fact]
    public void ALowConfidencePhotoIdentificationNamesWhatWasActuallyPriced()
    {
        var snap = Snap(50m);
        var identity = new SnapIdentity { Title = "Bitmain Antminer S19j Pro", Certainty = "low" };

        SnapJudge.AddIdentityWarnings(snap, identity);

        Assert.Contains(snap.Warnings, w => w.Contains("Bitmain Antminer S19j Pro") && w.Contains("photo"));
    }

    [Fact]
    public void AConfidentPhotoIdentificationStillSurfacesTheThingToCheckByHand()
    {
        // The one caveat that is not about confidence: a photo cannot tell you whether it powers on,
        // and that is the failure that costs the whole buy price.
        var snap = Snap(50m);
        var identity = new SnapIdentity
        {
            Title = "Bitmain Antminer S19j Pro", Certainty = "high", CheckThis = "Power it on before you pay",
        };

        SnapJudge.AddIdentityWarnings(snap, identity);

        Assert.Contains(snap.Warnings, w => w.Contains("Power it on before you pay"));
        Assert.DoesNotContain(snap.Warnings, w => w.Contains("not confidently"));
    }

    [Fact]
    public void ASilentIdentificationAddsNothing()
    {
        var snap = Snap(50m);
        SnapJudge.AddIdentityWarnings(snap, new SnapIdentity { Title = "x", Certainty = "high" });
        Assert.Empty(snap.Warnings);
    }
}
