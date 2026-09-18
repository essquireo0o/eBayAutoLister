using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;
using Xunit;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The deal judge is the layer that stops Auto-Buy buying junk: it prices a candidate against real
/// sold comps and reads its title for trouble words, and only a clean, provably-profitable listing
/// earns Buy. These pin every branch of that decision.
/// </summary>
public class AutoBuyDealJudgeTests
{
    private static EbayOpportunityItem Item(decimal price, string title = "Antminer S19 95TH", string cond = "USED_GOOD")
        => new() { Title = title, Price = price, Condition = cond, ItemId = "v1|1|0" };

    private static SoldCompsResult Comps(decimal median, int count)
        => new() { Median = median, Count = count, Average = median };

    // Fee model: resale*0.1325 + 0.40. Defaults: MinNet $15, MinRoi 20%, MinComps 3.

    [Fact]
    public void A_clean_profitable_listing_with_real_comps_is_a_Buy()
    {
        // $340 buy, ~$1256 median: ~$749 net, ~220% ROI, 4 comps, no flags.
        var j = AutoBuyDealJudge.Judge(Item(340m), Comps(1256m, 4), 15m, 20m);
        Assert.Equal(AutoBuyVerdict.Buy, j.Verdict);
        Assert.True(j.EstimatedNetProfit > 700m);
        Assert.True(j.RoiPercent > 150m);
        Assert.Contains("Buy", j.Reason);
    }

    [Fact]
    public void A_listing_that_loses_money_against_comps_is_a_Skip()
    {
        // Priced $900, resells ~$950: fees ~$126 wipe the margin out.
        var j = AutoBuyDealJudge.Judge(Item(900m), Comps(950m, 6), 15m, 20m);
        Assert.Equal(AutoBuyVerdict.Skip, j.Verdict);
        Assert.True(j.EstimatedNetProfit < 15m);
    }

    [Fact]
    public void A_thin_or_empty_comp_history_never_says_Buy()
    {
        Assert.Equal(AutoBuyVerdict.Caution, AutoBuyDealJudge.Judge(Item(100m), Comps(400m, 2), 15m, 20m).Verdict);
        Assert.Equal(AutoBuyVerdict.Caution, AutoBuyDealJudge.Judge(Item(100m), Comps(0m, 0), 15m, 20m).Verdict);
        Assert.Equal(AutoBuyVerdict.Caution, AutoBuyDealJudge.Judge(Item(100m), null, 15m, 20m).Verdict);
    }

    [Fact]
    public void A_hard_red_flag_is_a_Skip_even_when_the_math_is_great()
    {
        // Would be a huge margin, but the title says "for parts".
        var j = AutoBuyDealJudge.Judge(Item(50m, "Antminer S19 FOR PARTS not working"), Comps(1200m, 8), 15m, 20m);
        Assert.Equal(AutoBuyVerdict.Skip, j.Verdict);
        Assert.Contains("for parts", j.RedFlags);
    }

    [Fact]
    public void A_soft_flag_on_a_profitable_listing_drops_to_Caution()
    {
        var j = AutoBuyDealJudge.Judge(Item(340m, "Antminer S19 95TH untested"), Comps(1256m, 5), 15m, 20m);
        Assert.Equal(AutoBuyVerdict.Caution, j.Verdict);
        Assert.Contains("untested", j.RedFlags);
        Assert.True(j.EstimatedNetProfit > 700m); // the money is still computed and good
    }

    [Fact]
    public void The_profit_and_roi_floors_are_enforced()
    {
        // ~$60 net at ~$500 buy is real cash but only ~12% ROI — under a 20% floor.
        var thinRoi = AutoBuyDealJudge.Judge(Item(500m), Comps(650m, 5), 15m, 20m);
        Assert.Equal(AutoBuyVerdict.Skip, thinRoi.Verdict);
        // Same listing with a 5% floor clears.
        var ok = AutoBuyDealJudge.Judge(Item(500m), Comps(650m, 5), 15m, 5m);
        Assert.Equal(AutoBuyVerdict.Buy, ok.Verdict);
    }

    [Fact]
    public void Red_flag_detection_finds_the_words()
    {
        var flags = AutoBuyDealJudge.RedFlagsIn("Untested unit, sold AS-IS, cracked case");
        Assert.Contains("as-is", flags);   // "AS-IS" lower-cases to the hyphenated flag
        Assert.Contains("untested", flags);
        Assert.Contains("cracked", flags);
        Assert.Contains("as is", AutoBuyDealJudge.RedFlagsIn("sold as is, no returns")); // spaced form too
        Assert.Empty(AutoBuyDealJudge.RedFlagsIn("Clean working Antminer S19, tested and running"));
    }
}
