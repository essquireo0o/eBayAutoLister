using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The money that is actually there. Sixteen sessions of this card have sharpened one question —
/// what is the thing on screen WORTH — and every one of them assumed the seller could pay whatever
/// the answer came to. A live show is where that assumption breaks inside an hour: the lots come
/// every four minutes, each one individually defensible, and the card says <c>BID UP TO</c> on all
/// of them.
/// </summary>
/// <remarks>
/// <para>
/// The two properties these tests exist to hold in place. <b>Nothing is assumed</b>: an empty budget
/// box caps nothing whatever is on tonight's sheet, so a card without one is priced exactly as it was
/// before this file existed. And <b>the market is never touched</b>: the item is worth what the comps
/// say it is worth — there is simply not enough left to land it — so this cuts the ceiling and says
/// so in cash, and a lot refused for money is never described as a lot the market refused.
/// </para>
/// </remarks>
public class LiveBudgetTests
{
    private static LiveBudgetRead Read(
        decimal? budget, decimal spent, int lots, decimal marketCeiling,
        decimal fee = 0m, decimal shipping = 0m, decimal tax = 0m) =>
        LiveBudget.Read(budget, new LiveBudgetTonight(lots, spent), marketCeiling, fee, shipping, tax);

    // ── Nothing set ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// No budget caps nothing, whatever has been spent — the ceiling stays the market's, which is
    /// the property that makes every card built before this file existed identical to one built now.
    /// </summary>
    [Fact]
    public void No_budget_caps_nothing_however_much_has_been_spent()
    {
        var read = Read(null, spent: 1_400m, lots: 6, marketCeiling: 90m);

        Assert.Equal(LiveBudgetVerdicts.None, read.Verdict);
        Assert.False(read.Stated);
        Assert.False(read.Applied);
        Assert.False(read.Capped);
        Assert.False(read.Exhausted);
        Assert.Equal(90m, read.Ceiling);
        Assert.Equal(0m, read.CutPercent);
    }

    /// <summary>
    /// The spend is still reported. It is a fact about the account rather than an assumption about
    /// the seller, and the whole point of the feature is that six defensible calls in one hour is a
    /// number nobody was carrying.
    /// </summary>
    [Fact]
    public void No_budget_still_says_what_the_night_has_cost()
    {
        var read = Read(null, spent: 1_400m, lots: 6, marketCeiling: 90m);

        Assert.Equal(1_400m, read.Committed);
        Assert.Equal(6, read.LotsWon);
        Assert.Contains("$1,400", read.Headline, StringComparison.Ordinal);
        Assert.Contains("6 lots", read.Warning, StringComparison.Ordinal);
    }

    /// <summary>The first lot of the night is not warned about. There is nothing to warn about: no
    /// budget, no spend, and a ceiling that is exactly what it always was.</summary>
    [Fact]
    public void No_budget_and_no_spend_says_nothing_on_the_warning_list()
    {
        var read = Read(null, spent: 0m, lots: 0, marketCeiling: 90m);

        Assert.Equal(LiveBudgetVerdicts.None, read.Verdict);
        Assert.Equal("", read.Warning);
        Assert.Equal(90m, read.Ceiling);
    }

    /// <summary>A zero is not a budget of zero. Nobody sets one, and reading it as "you may spend
    /// nothing" would refuse every lot on the screen for a keystroke.</summary>
    [Fact]
    public void A_zero_budget_is_no_budget()
    {
        Assert.Equal(0m, LiveBudget.Sanitize(0m));
        Assert.Equal(0m, LiveBudget.Sanitize(null));
        Assert.Equal(0m, LiveBudget.Sanitize(-50m));

        Assert.Equal(LiveBudgetVerdicts.None, Read(0m, 0m, 0, 90m).Verdict);
    }

    /// <summary>An extra zero on a night's buying is a typo. Clamped rather than rejected, so it
    /// costs a cap that never binds instead of the answer.</summary>
    [Fact]
    public void An_absurd_budget_is_clamped_rather_than_refused()
    {
        Assert.Equal(LiveBudget.MaxBudget, LiveBudget.Sanitize(50_000_000m));
        Assert.Equal(500m, LiveBudget.Sanitize(500m));
    }

    // ── Room to spare ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_budget_with_room_left_cuts_nothing_and_says_the_figure()
    {
        var read = Read(500m, spent: 120m, lots: 2, marketCeiling: 90m);

        Assert.Equal(LiveBudgetVerdicts.Clear, read.Verdict);
        Assert.True(read.Stated);
        Assert.False(read.Applied);
        Assert.Equal(380m, read.Remaining);
        Assert.Equal(24m, read.SpentPercent);
        Assert.Equal(90m, read.Ceiling);
        Assert.Equal(0m, read.CutPercent);

        // An accurate card is not warned about.
        Assert.Equal("", read.Warning);
    }

    /// <summary>
    /// The one thing worth interrupting for in a state that cuts nothing: the budget is nearly gone,
    /// and it is the NEXT lot this stops rather than this one.
    /// </summary>
    [Fact]
    public void A_budget_nearly_gone_says_so_while_this_lot_still_fits()
    {
        var read = Read(500m, spent: 420m, lots: 5, marketCeiling: 60m);

        Assert.Equal(LiveBudgetVerdicts.Clear, read.Verdict);
        Assert.Equal(60m, read.Ceiling);
        Assert.Equal(84m, read.SpentPercent);
        Assert.Contains("84%", read.Warning, StringComparison.Ordinal);
        Assert.Contains("the one after it may not", read.Warning, StringComparison.Ordinal);
    }

    // ── The cash is the ceiling ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The state that moves money. The comps say $200 and there is $62 left, so the ceiling is $62 —
    /// and both figures are on the read, because "this lot is not worth it" and "this lot is worth it
    /// and you cannot afford it" send a seller in opposite directions.
    /// </summary>
    [Fact]
    public void The_cash_left_becomes_the_ceiling_and_both_figures_survive()
    {
        var read = Read(500m, spent: 438m, lots: 6, marketCeiling: 200m);

        Assert.Equal(LiveBudgetVerdicts.Capped, read.Verdict);
        Assert.True(read.Applied);
        Assert.True(read.Capped);
        Assert.False(read.Exhausted);

        Assert.Equal(62m, read.Remaining);
        Assert.Equal(62m, read.Ceiling);
        Assert.Equal(200m, read.MarketCeiling);
        Assert.Equal(69m, read.CutPercent);

        Assert.Contains("$62", read.Headline, StringComparison.Ordinal);
        Assert.Contains("$200", read.Note, StringComparison.Ordinal);
        Assert.Contains("$200", read.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// What is left has to cover the WHOLE landed cost — the bid, the premium on it, the tax on both
    /// and the freight. So the affordable bid is the remaining cash run through the app's own
    /// all-in-to-bid conversion, not the cash itself. Asserted against that function directly: a
    /// second arithmetic here is how the badge and the ladder end up disagreeing by the premium.
    /// </summary>
    [Fact]
    public void The_affordable_bid_is_the_remaining_cash_through_the_cards_own_conversion()
    {
        var read = Read(500m, spent: 438m, lots: 6, marketCeiling: 200m, fee: 8m, shipping: 12m, tax: 9m);

        Assert.Equal(LiveBidAdvisor.BreakEvenBid(62m, 8m, 12m, 9m), read.Affordable);
        Assert.Equal(read.Affordable, read.Ceiling);

        // And winning at exactly that bid lands inside what is left, to the cent.
        Assert.True(LiveBidAdvisor.LandedCost(read.Ceiling, 8m, 12m, 9m) <= read.Remaining,
            $"a {read.Ceiling} bid lands at {LiveBidAdvisor.LandedCost(read.Ceiling, 8m, 12m, 9m)}, " +
            $"over the {read.Remaining} left");
    }

    // ── Nothing left ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_spent_budget_stops_the_card_and_keeps_the_market_ceiling_beside_it()
    {
        var read = Read(500m, spent: 500m, lots: 7, marketCeiling: 200m);

        Assert.Equal(LiveBudgetVerdicts.Spent, read.Verdict);
        Assert.True(read.Exhausted);
        Assert.False(read.Capped);
        Assert.Equal(0m, read.Ceiling);
        Assert.Equal(200m, read.MarketCeiling);
        Assert.Equal(100m, read.CutPercent);
        Assert.Equal(100m, read.SpentPercent);

        // The badge's sentence says the lot is fine and the money is not.
        Assert.Contains("Nothing wrong with the lot", read.Reason, StringComparison.Ordinal);
        Assert.Contains("$200", read.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Money left, and none of it can land anything: the freight alone is more than the remainder.
    /// Its own sentence, because a seller staring at a $6 lot being refused with $8 in the budget
    /// needs the shipping named.
    /// </summary>
    [Fact]
    public void Cash_that_cannot_even_cover_the_freight_is_a_spent_budget_with_its_own_sentence()
    {
        var read = Read(500m, spent: 492m, lots: 9, marketCeiling: 200m, fee: 8m, shipping: 12m);

        Assert.Equal(LiveBudgetVerdicts.Spent, read.Verdict);
        Assert.Equal(8m, read.Remaining);
        Assert.Equal(0m, read.Affordable);
        Assert.Equal(0m, read.Ceiling);

        Assert.Contains("not enough to land a lot", read.Headline, StringComparison.Ordinal);
        Assert.Contains("$12", read.Note, StringComparison.Ordinal);
    }

    /// <summary>Overspent is not negative. A seller who blew past their own limit is at zero left,
    /// not at minus eighty — the ceiling arithmetic above has no meaning for a negative wallet.</summary>
    [Fact]
    public void An_overspent_budget_floors_at_nothing_left()
    {
        var read = Read(500m, spent: 580m, lots: 8, marketCeiling: 200m);

        Assert.Equal(0m, read.Remaining);
        Assert.Equal(100m, read.SpentPercent);
        Assert.Equal(LiveBudgetVerdicts.Spent, read.Verdict);
    }

    // ── What it refuses to do ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A lot the market itself refused is never described as a lot the wallet refused. With no
    /// ceiling to cut, nothing is applied — which is what lets <c>Judge</c> keep saying <c>DON'T
    /// BID</c> about a bad lot on a night the money also happens to have run out.
    /// </summary>
    [Fact]
    public void A_lot_the_market_already_refused_is_never_blamed_on_the_money()
    {
        var read = Read(500m, spent: 500m, lots: 7, marketCeiling: 0m);

        Assert.Equal(LiveBudgetVerdicts.Spent, read.Verdict);
        Assert.False(read.Applied);
        Assert.False(read.Exhausted);
        Assert.False(read.Capped);
        Assert.Equal(0m, read.CutPercent);
        Assert.Equal("", read.Reason);
    }

    /// <summary>
    /// It prices nothing about the market. The read takes a budget, a spend, a ceiling and the three
    /// costs the ceiling was already built from — and no comps at all. A cash limit that could see
    /// the sold history would be a cash limit that could re-rate it.
    /// </summary>
    [Fact]
    public void The_read_never_sees_a_comp()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", "Services", "LiveBudget.cs"));

        Assert.DoesNotContain("Comparable", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MarketAnalysisResult", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ResalePricing", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SellThrough", source, StringComparison.Ordinal);
    }

    /// <summary>The four states are spelled once, so the strip, the speech, the CSS, the log and
    /// these tests cannot drift apart.</summary>
    [Fact]
    public void The_four_states_are_spelled_once()
    {
        var models = File.ReadAllText(
            Path.Combine(RepoRoot(), "ING eBay AutoLister", "Models", "LiveBudgetModels.cs"));

        foreach (var state in new[] { "none", "clear", "capped", "spent" })
            Assert.Contains($"= \"{state}\";", models, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
