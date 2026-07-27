using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// "Stacking" is the part of couponing people get wrong with real money, and every way of getting it
// wrong makes the buy look cheaper than it is: two codes are not two discounts, a sitewide sale is
// already in the shelf price, and cashback is not money you have while the item is still on the
// shelf.
//
// These pin the cost basis the profit maths is handed. Every one of them errs toward charging more,
// which is the safe direction for a number somebody spends money on.
public class CouponStackerTests
{
    private static readonly DateTime Now = new(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);

    private static CouponOffer Code(string code, decimal percent, decimal minSpend = 0m,
        string confidence = CouponConfidence.Medium) => new()
    {
        MerchantId = "homedepot", MerchantLabel = "The Home Depot",
        Kind = CouponKinds.PercentOff, Code = code, Value = percent, MinSpend = minSpend,
        Confidence = confidence, PublishedUtc = Now.AddDays(-2), Title = $"{percent}% off with {code}",
        // Sitewide. An item's own code is a different thing entirely — see the test below.
        AppliesToOrder = true,
    };

    private static CouponOffer Dollars(string code, decimal amount, decimal minSpend = 0m) => new()
    {
        MerchantId = "homedepot", MerchantLabel = "The Home Depot",
        Kind = CouponKinds.AmountOff, Code = code, Value = amount, MinSpend = minSpend,
        Confidence = CouponConfidence.Medium, PublishedUtc = Now.AddDays(-2), AppliesToOrder = true,
    };

    private static CouponOffer Cashback(decimal percent) => new()
    {
        MerchantId = "homedepot", MerchantLabel = "The Home Depot via Rakuten",
        Kind = CouponKinds.Cashback, Value = percent,
        Confidence = CouponConfidence.Medium, PublishedUtc = Now.AddDays(-1), AppliesToOrder = true,
    };

    // ── One code per order ────────────────────────────────────────────────────

    [Fact]
    public void OnlyOneCodeIsEverApplied()
    {
        // Two 20% codes are not 40%. Every checkout on earth takes one.
        var stack = CouponStacker.Best([Code("A20", 20m), Code("B15", 15m)], 200m, 0m, nowUtc: Now);

        Assert.Single(stack.Applied);
        Assert.Equal(40m, stack.Discount);
        Assert.Equal(160m, stack.DiscountedSubtotal);
    }

    [Fact]
    public void TheCodeAppliedIsTheOneWorthMost()
    {
        var stack = CouponStacker.Best([Code("SMALL", 5m), Dollars("BIG", 60m, minSpend: 100m)], 200m, 0m, nowUtc: Now);

        Assert.Equal("BIG", Assert.Single(stack.Applied).Code);
        Assert.Equal(60m, stack.Discount);
    }

    [Fact]
    public void TheOnesNotAppliedAreStillShown()
    {
        // A code this app declined to bank may still be the right one for the seller's actual
        // basket. Hiding it would be the app deciding for them.
        var stack = CouponStacker.Best([Code("A20", 20m), Code("B15", 15m)], 200m, 0m, nowUtc: Now);

        Assert.Equal("B15", Assert.Single(stack.AlsoFound).Code);
    }

    [Fact]
    public void APriceThatAlreadyNeedsACodeCannotTakeASecondOne()
    {
        // The advertised price is only that price with its own code typed in. A store-wide code
        // cannot go in the same box.
        var stack = CouponStacker.Best([Code("SITEWIDE20", 20m)], 200m, 0m, existingCode: "DEALCODE", nowUtc: Now);

        Assert.Empty(stack.Applied);
        Assert.Equal(0m, stack.Discount);
        Assert.Contains("one code", stack.Note);
        // Still shown, because it may be worth more than the deal's own code.
        Assert.Single(stack.AlsoFound);
    }

    // ── What can't be counted ─────────────────────────────────────────────────

    [Fact]
    public void ADiscountWithNoCodeIsAlreadyInTheShelfPrice()
    {
        var sale = Code("", 25m);
        var stack = CouponStacker.Best([sale], 200m, 0m, nowUtc: Now);

        Assert.Empty(stack.Applied);
        Assert.Equal(0m, stack.Discount);
    }

    [Fact]
    public void AnImplausiblePercentageIsAClearanceHeadlineNotACode()
    {
        // Above half off, a "sitewide code" is a category sale or a misparse — and a fabricated 70%
        // cut to a cost basis would put an imaginary goldmine at the top of the board.
        Assert.False(CouponStacker.BankableDiscount(Code("HUGE", 70m), 200m));
        Assert.True(CouponStacker.BankableDiscount(Code("REAL", 20m), 200m));
    }

    [Fact]
    public void AnUngatedDollarCodeCannotSwallowTheItem()
    {
        // "$100 off" beside a $120 item is "$100 off $1,000" with the threshold written somewhere
        // the parser couldn't reach.
        Assert.False(CouponStacker.BankableDiscount(Dollars("HUNDRED", 100m), 120m));
        // The same code with its threshold stated is fine, once the item actually reaches it.
        Assert.True(CouponStacker.BankableDiscount(Dollars("HUNDRED", 100m, minSpend: 500m), 600m));
    }

    [Fact]
    public void ACodeGatedAboveThisPriceIsNotApplied()
    {
        var stack = CouponStacker.Best([Dollars("SPEND", 50m, minSpend: 500m)], 200m, 0m, nowUtc: Now);

        Assert.Empty(stack.Applied);
        // Shown though: a seller buying three of them does reach the threshold.
        Assert.Single(stack.AlsoFound);
    }

    [Fact]
    public void ACodeBoundToSomebodyElsesDealIsNotEvenOfferedAsAnAlternative()
    {
        // Most codes on a store's coupon list are posted against one specific listing. Against any
        // other item at that store they buy nothing, so they are not shown beside a row that would
        // imply they could be used on it.
        var itemCode = Code("LUSF2737", 20m);
        itemCode.AppliesToOrder = false;

        var stack = CouponStacker.Best([itemCode], 200m, 0m, nowUtc: Now);

        Assert.Empty(stack.Applied);
        Assert.Empty(stack.AlsoFound);
        Assert.Equal(RetailBuyCosts.AllInCost(200m, 0m), stack.NetCost);
    }

    [Fact]
    public void AnExpiredCodeIsGoneRatherThanGreyedOut()
    {
        var dead = Code("OLD20", 20m);
        dead.ExpiresUtc = Now.AddDays(-1);

        var stack = CouponStacker.Best([dead], 200m, 0m, nowUtc: Now);

        Assert.Empty(stack.Applied);
        Assert.Empty(stack.AlsoFound);
    }

    [Fact]
    public void FreeShippingIsSurfacedAndNeverCounted()
    {
        var shipping = new CouponOffer
        {
            Kind = CouponKinds.FreeShipping, Code = "SHIPFREE", MerchantId = "homedepot",
            Confidence = CouponConfidence.Medium, PublishedUtc = Now.AddDays(-1), AppliesToOrder = true,
        };

        var stack = CouponStacker.Best([shipping], 200m, 0m, nowUtc: Now);

        // What it saves depends on what they were going to charge to ship it, which isn't published.
        Assert.Equal(0m, stack.Discount);
        Assert.Single(stack.AlsoFound);
        Assert.Contains("free-shipping", stack.Note);
    }

    // ── Sales tax follows the discount ────────────────────────────────────────

    [Fact]
    public void ACodeSavesItsFaceValuePlusTheTaxThatSatOnTop()
    {
        var stack = CouponStacker.Best([Code("SAVE20", 20m)], 200m, 7.5m, nowUtc: Now);

        Assert.Equal(40m, stack.Discount);
        Assert.Equal(160m, stack.DiscountedSubtotal);
        // A register charges tax on what it rings up, and it rings up the discounted price.
        Assert.Equal(12m, stack.SalesTax);
        Assert.Equal(172m, stack.NetCost);
        // Against $215 all-in without the code: $43 saved on a $40 code.
        Assert.Equal(43m, RetailBuyCosts.AllInCost(200m, 7.5m) - stack.NetCost);
    }

    [Fact]
    public void NoOffersLeavesTheBuyCostingExactlyWhatItDidBefore()
    {
        var stack = CouponStacker.Best([], 200m, 7.5m, nowUtc: Now);

        Assert.False(stack.HasSaving);
        Assert.Equal(RetailBuyCosts.AllInCost(200m, 7.5m), stack.NetCost);
        Assert.Contains("No public codes", stack.Note);
    }

    // ── Cashback ──────────────────────────────────────────────────────────────

    [Fact]
    public void CashbackStacksWithACodeBecauseSomebodyElsePaysIt()
    {
        var stack = CouponStacker.Best([Code("SAVE20", 20m), Cashback(10m)], 200m, 0m, nowUtc: Now);

        Assert.Equal(2, stack.Applied.Count);
        // Paid on what was actually spent — a portal does not pay a percentage of a price nobody
        // was charged.
        Assert.Equal(16m, stack.CashbackExpected);
    }

    [Fact]
    public void PartOfACashbackClaimIsHeldBackAndTheWaitIsStated()
    {
        var stack = CouponStacker.Best([Cashback(10m)], 200m, 0m, nowUtc: Now);

        Assert.Equal(20m, stack.CashbackExpected);
        Assert.Equal(3m, stack.CashbackReserve);          // 15% of the claim, not counted as money
        Assert.Equal(183m, stack.NetCost);                // $200 - $20 + $3
        Assert.Equal(CouponStacker.CashbackWaitDays, stack.CashbackWaitDays);
        Assert.Contains("days after the order", stack.Note);
    }

    [Fact]
    public void CashbackStacksEvenWhenTheDealsOwnCodeBlocksEverythingElse()
    {
        var stack = CouponStacker.Best([Code("SITEWIDE", 20m), Cashback(6m)], 200m, 0m,
            existingCode: "DEALCODE", nowUtc: Now);

        Assert.Equal(0m, stack.Discount);
        Assert.Equal(12m, stack.CashbackExpected);
        Assert.Equal(CouponKinds.Cashback, Assert.Single(stack.Applied).Kind);
    }

    [Fact]
    public void AnAbsurdCashbackRateIsReadOffSomethingElse()
    {
        Assert.False(CouponStacker.BankableCashback(Cashback(60m)));
        Assert.False(CouponStacker.BankableCashback(Cashback(0.1m)));
        Assert.True(CouponStacker.BankableCashback(Cashback(8m)));
    }

    // ── Confidence ────────────────────────────────────────────────────────────

    [Fact]
    public void AStackIsOnlyAsTrustworthyAsItsWeakestPart()
    {
        var stack = CouponStacker.Best(
            [Code("SURE", 20m, confidence: CouponConfidence.High), Cashback(6m)], 200m, 0m, nowUtc: Now);

        Assert.Equal(CouponConfidence.Medium, stack.Confidence);
    }

    [Fact]
    public void ALowConfidenceStackSaysSoBesideTheMoney()
    {
        var shaky = Code("MAYBE20", 20m, confidence: CouponConfidence.Low);
        shaky.ConfidenceNote = "Conditions attached: only on selected items.";

        var stack = CouponStacker.Best([shaky], 200m, 0m, nowUtc: Now);

        Assert.Equal(40m, stack.Discount);
        Assert.Contains("lead rather than a price", stack.Note);
    }
}
