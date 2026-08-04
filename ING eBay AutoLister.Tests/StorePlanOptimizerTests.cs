using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The Store Plan is the one recommendation in this app that costs the seller real money the moment
/// it is wrong in either direction: told to downgrade when they should not, they start paying
/// insertion fees on every listing they own; told to upgrade when they should not, they add a bill
/// that never pays for itself. There is no market, no comp and no forecast in any of it — it is
/// arithmetic over a published rate card, which means every one of these is checkable by hand.
/// </summary>
public class StorePlanOptimizerTests
{
    private static readonly StorePlanOptimizer Optimizer = new();

    private static StorePlanResult Evaluate(
        int listings, string plan = "none", bool annual = true, int? overrideCount = null,
        decimal monthlySales = 0m, bool measured = true) =>
        Optimizer.Evaluate(new StorePlanInputs
        {
            ActiveListings = listings,
            ListingCountMeasured = measured,
            ListingsOverride = overrideCount,
            CurrentPlanKey = plan,
            AnnualBilling = annual,
            MonthlySales = monthlySales,
        });

    private static StorePlanOption Option(StorePlanResult result, string key) =>
        result.Options.Single(o => o.Key == key);

    // ── The money ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Nine_hundred_listings_with_no_store_is_the_case_this_feature_exists_for()
    {
        // 900 live listings, 250 of them free, 650 × $0.35 = $227.50 a month in insertion fees, for
        // nothing. A Basic Store is $21.95 and covers a thousand. This is the single largest sum in
        // the app that a seller can collect by clicking one radio button on eBay.
        var result = Evaluate(900, plan: "none");

        Assert.Equal(227.50m, result.CurrentMonthlyCost);
        Assert.Equal("basic", result.BestPlanKey);
        Assert.Equal(21.95m, result.BestMonthlyCost);
        Assert.Equal(205.55m, result.MonthlySaving);
        Assert.Equal(2_466.60m, result.AnnualSaving);
        Assert.False(result.AlreadyOnBestPlan);
    }

    [Fact]
    public void A_store_bought_for_a_catalogue_that_shrank_is_priced_the_same_way_round()
    {
        // The other half of the feature, and the half sellers never look for: 214 listings fit
        // inside the free 250 everybody gets, so the Basic Store is $21.95 a month buying nothing.
        var result = Evaluate(214, plan: "basic");

        Assert.Equal(21.95m, result.CurrentMonthlyCost);
        Assert.Equal("none", result.BestPlanKey);
        Assert.Equal(0m, result.BestMonthlyCost);
        Assert.Equal(21.95m, result.MonthlySaving);
        Assert.Equal(263.40m, result.AnnualSaving);
    }

    [Fact]
    public void The_seller_already_on_the_right_plan_is_told_so_and_nothing_is_invented()
    {
        var result = Evaluate(200, plan: "none");

        Assert.True(result.AlreadyOnBestPlan);
        Assert.Equal(0m, result.MonthlySaving);
        Assert.Equal(0m, result.TotalAnnualSaving);
        Assert.Contains("cheapest plan there is", result.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_row_carries_its_own_arithmetic_in_dollars()
    {
        var result = Evaluate(900, plan: "none");
        var none = Option(result, "none");

        Assert.Equal(650, none.ListingsCharged);
        Assert.Equal(227.50m, none.InsertionCost);
        Assert.Equal(227.50m, none.MonthlyCost);
        Assert.Equal(2_730.00m, none.AnnualCost);
        Assert.Contains("650 × $0.35", none.Basis, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tier_whose_allotment_covers_everything_charges_no_insertion_fees()
    {
        var premium = Option(Evaluate(900, plan: "none"), "premium");

        Assert.Equal(0, premium.ListingsCharged);
        Assert.Equal(0m, premium.InsertionCost);
        Assert.Equal(59.95m, premium.MonthlyCost);
    }

    [Fact]
    public void The_delta_on_every_row_is_measured_against_the_plan_the_seller_is_on()
    {
        var result = Evaluate(900, plan: "none");

        Assert.Equal(0m, Option(result, "none").MonthlyDelta);          // the current plan itself
        Assert.Equal(-205.55m, Option(result, "basic").MonthlyDelta);   // cheaper
        Assert.Equal(2_772.45m, Option(result, "enterprise").MonthlyDelta);  // far dearer
        Assert.True(Option(result, "none").IsCurrent);
        Assert.True(Option(result, "basic").IsBest);
    }

    // ── The billing cycle, which is a saving with nothing given up for it ─────────────────────

    [Fact]
    public void Paying_annually_for_the_same_tier_is_reported_even_when_the_tier_is_already_right()
    {
        // 900 listings puts Basic in the right place on either cycle, so there is no plan change to
        // make — and $6 a month is still sitting there for committing to a plan they are staying on.
        var result = Evaluate(900, plan: "basic", annual: false);

        Assert.True(result.AlreadyOnBestPlan);
        Assert.Equal(0m, result.MonthlySaving);
        Assert.Equal(6.00m, result.BillingMonthlySaving);
        Assert.Equal(72.00m, result.TotalAnnualSaving);
        Assert.Contains("same allotment, same fees", result.BillingNote, StringComparison.Ordinal);
        Assert.Contains("annual rate", result.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void Nobody_paying_nothing_is_told_to_commit_for_a_year()
    {
        // "No store" costs $0 on both cycles. A billing-cycle prompt here would be pure noise, and
        // noise on a screen whose whole value is one clear instruction is what kills the screen.
        var result = Evaluate(100, plan: "none", annual: false);

        Assert.Equal(0m, result.BillingMonthlySaving);
        Assert.Equal("", result.BillingNote);
    }

    [Fact]
    public void A_seller_already_on_the_annual_rate_is_told_that_rather_than_nothing()
    {
        var result = Evaluate(900, plan: "basic", annual: true);

        Assert.Equal(0m, result.BillingMonthlySaving);
        Assert.Contains("already on the annual rate", result.BillingNote, StringComparison.Ordinal);
    }

    [Fact]
    public void The_plan_change_and_the_billing_change_are_added_rather_than_reported_separately()
    {
        // A seller on no store, billed monthly, with 900 listings: move to Basic ($227.50 → $27.95)
        // and pay annually ($27.95 → $21.95). Only the two together are what the switch is worth.
        var result = Evaluate(900, plan: "none", annual: false);

        Assert.Equal(199.55m, result.MonthlySaving);
        Assert.Equal(6.00m, result.BillingMonthlySaving);
        Assert.Equal(2_466.60m, result.TotalAnnualSaving);   // (199.55 + 6.00) × 12
    }

    // ── The ladder: where the next change lands ──────────────────────────────────────────────

    [Theory]
    [InlineData(0, "none")]
    [InlineData(250, "none")]
    [InlineData(312, "none")]
    [InlineData(313, "basic")]
    [InlineData(1_152, "basic")]
    [InlineData(1_153, "premium")]
    [InlineData(12_400, "premium")]
    [InlineData(12_401, "anchor")]
    [InlineData(79_000, "anchor")]
    [InlineData(79_001, "enterprise")]
    public void The_cheapest_tier_at_a_given_listing_count_is_worked_out_to_the_listing(
        int listings, string expected)
    {
        // Every one of these is a hand-checkable crossover. 313, for instance: at 312 listings the
        // free-for-everybody allotment leaves 62 chargeable at $0.35 = $21.70, a nickel under the
        // Basic Store's $21.95. One more listing and the store is cheaper, forever after.
        Assert.Equal(expected, StorePlanCatalog.Cheapest(listings, annualBilling: true).Key);
    }

    [Fact]
    public void A_tie_goes_to_the_smaller_commitment()
    {
        // At exactly 1,152 listings Basic and Premium both cost $59.95. Recommending the $59.95/mo
        // subscription on a coin toss is how a seller ends up worse off for following the advice.
        Assert.Equal(59.95m, StorePlanCatalog.MonthlyCost(StorePlanCatalog.Resolve("basic"), 1_152, true));
        Assert.Equal(59.95m, StorePlanCatalog.MonthlyCost(StorePlanCatalog.Resolve("premium"), 1_152, true));
        Assert.Equal("basic", StorePlanCatalog.Cheapest(1_152, true).Key);
    }

    [Fact]
    public void Each_tier_reports_the_band_of_listing_counts_it_is_the_cheapest_over()
    {
        var result = Evaluate(900, plan: "none");

        var none = Option(result, "none");
        Assert.Equal(0, none.CheapestFrom);
        Assert.Equal(312, none.CheapestTo);

        var basic = Option(result, "basic");
        Assert.Equal(313, basic.CheapestFrom);
        Assert.Equal(1_152, basic.CheapestTo);

        // The top of the ladder has no ceiling — past it there is simply nothing bigger to move to.
        var enterprise = Option(result, "enterprise");
        Assert.Equal(79_001, enterprise.CheapestFrom);
        Assert.Null(enterprise.CheapestTo);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_starter_store_is_never_the_cheapest_plan_on_either_billing_cycle(bool annual)
    {
        // Not an opinion — arithmetic. Starter's allotment is the same 250 free listings every
        // seller gets without paying anything, so by the time its cheaper insertion fee has repaid
        // the subscription, the Basic Store's thousand-listing allotment has already won. Saying so
        // out loud is worth more than the row: it is the tier sellers buy first.
        var starter = Option(Evaluate(900, plan: "none", annual: annual), "starter");

        Assert.True(starter.NeverCheapest);
        Assert.DoesNotContain(
            StorePlanCatalog.Ladder(annual).Keys, k => k.Equals("starter", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_next_step_says_how_much_headroom_is_left_before_the_plan_changes_again()
    {
        var result = Evaluate(900, plan: "none");

        Assert.Contains("1,152", result.NextStep, StringComparison.Ordinal);
        Assert.Contains("252 more", result.NextStep, StringComparison.Ordinal);
        Assert.Contains("Premium Store", result.NextStep, StringComparison.Ordinal);
    }

    [Fact]
    public void At_the_top_of_the_ladder_there_is_nothing_to_grow_into_and_it_says_so()
    {
        var result = Evaluate(90_000, plan: "enterprise");

        Assert.Equal("enterprise", result.BestPlanKey);
        Assert.Contains("however many more listings", result.NextStep, StringComparison.Ordinal);
    }

    // ── What the count is, and where it came from ────────────────────────────────────────────

    [Fact]
    public void A_count_the_seller_typed_beats_the_one_ebay_reported()
    {
        // The scale-up question — "what should I be on if I get to 1,500?" — is the second reason
        // to open this screen, and it has to be answerable without listing 600 more items first.
        var result = Evaluate(900, plan: "basic", overrideCount: 1_500);

        Assert.Equal(1_500, result.ListingsPerMonth);
        Assert.Equal(900, result.ActiveListings);
        Assert.True(result.UsingOverride);
        Assert.Equal("premium", result.BestPlanKey);
    }

    [Fact]
    public void Planning_against_a_made_up_number_is_said_out_loud_rather_than_left_to_be_noticed()
    {
        var result = Evaluate(900, plan: "basic", overrideCount: 1_500);

        Assert.Contains(result.Honesty, h =>
            h.Contains("1,500", StringComparison.Ordinal) && h.Contains("900", StringComparison.Ordinal));
    }

    [Fact]
    public void A_count_that_did_not_come_from_ebay_is_never_presented_as_though_it_had()
    {
        var typed = Evaluate(900, plan: "none", measured: false);
        var counted = Evaluate(900, plan: "none", measured: true);

        Assert.Contains(typed.Honesty, h => h.Contains("the one you typed", StringComparison.Ordinal));
        Assert.DoesNotContain(counted.Honesty, h => h.Contains("the one you typed", StringComparison.Ordinal));
    }

    [Fact]
    public void An_override_past_the_top_of_the_rate_card_is_clamped_rather_than_extrapolated()
    {
        // Past Enterprise's allotment the card stops describing anything, and a band worked out
        // beyond it would be an invented rate rather than a published one.
        var result = Evaluate(0, plan: "none", overrideCount: 5_000_000);

        Assert.Equal(StorePlanCatalog.LadderCeiling, result.ListingsPerMonth);
    }

    [Fact]
    public void With_nothing_live_the_screen_says_so_instead_of_recommending_a_downgrade()
    {
        var result = Evaluate(0, plan: "basic");

        Assert.Contains("Nothing is live yet", result.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_plan_key_falls_back_to_no_store_rather_than_to_a_paid_tier()
    {
        // The safe default in both directions: it is what a seller who has never chosen is on, and
        // guessing a paid tier would invent a saving out of a bill they are not paying.
        var result = Evaluate(900, plan: "platinum-deluxe");

        Assert.Equal("none", result.CurrentPlanKey);
        Assert.Equal(227.50m, result.CurrentMonthlyCost);
    }

    // ── Scale, and the things it refuses to claim ────────────────────────────────────────────

    [Fact]
    public void The_bill_is_put_against_the_sales_it_comes_out_of()
    {
        var result = Evaluate(900, plan: "basic", monthlySales: 4_390m);

        Assert.Equal(0.50m, result.CostShareOfSalesPercent);   // $21.95 of $4,390
    }

    [Fact]
    public void With_no_sales_on_record_the_share_is_left_at_zero_rather_than_guessed()
    {
        Assert.Equal(0m, Evaluate(900, plan: "basic").CostShareOfSalesPercent);
    }

    [Fact]
    public void Final_value_fees_are_named_as_the_thing_this_does_not_model()
    {
        // The comparison is honest only because it is narrow. A seller who thinks this weighed
        // their selling fees would take the answer to mean more than it does.
        Assert.Contains(Evaluate(900).Honesty, h =>
            h.Contains("Final value fees are not in here", StringComparison.Ordinal));
    }

    [Fact]
    public void The_renewal_rule_the_whole_screen_rests_on_is_stated_every_time()
    {
        // "Listings you keep live" only equals "listings you are billed for" because each fixed-price
        // listing renews every 30 days and each renewal spends an allotment slot. A seller who does
        // not know that cannot judge whether the count above is the right one.
        Assert.Contains(Evaluate(900).Honesty, h =>
            h.Contains("renews every 30 days", StringComparison.Ordinal));
    }

    [Fact]
    public void Auctions_are_excluded_and_say_so_rather_than_being_quietly_counted()
    {
        Assert.Contains(Evaluate(900).Honesty, h =>
            h.Contains("Auction-style listings have their own allotments", StringComparison.Ordinal));
    }

    [Fact]
    public void The_rate_card_is_dated_by_a_warning_rather_than_presented_as_permanent()
    {
        var result = Evaluate(900);

        Assert.Contains("check them on eBay's fee pages", result.RatesNote, StringComparison.Ordinal);
        Assert.Contains(result.Honesty, h => h == StorePlanCatalog.RatesNote);
    }

    [Fact]
    public void Every_tier_is_costed_even_the_ones_that_could_never_win()
    {
        // A comparison that hides the tiers it rejected is one the seller has to take on trust.
        var result = Evaluate(900);

        Assert.Equal(StorePlanCatalog.Tiers.Count, result.Options.Count);
        Assert.All(result.Options, o => Assert.False(string.IsNullOrWhiteSpace(o.Basis)));
        Assert.All(result.Options, o => Assert.False(string.IsNullOrWhiteSpace(o.Unlocks)));
    }

    [Fact]
    public void The_tier_ebay_sells_annually_only_is_flagged_rather_than_hidden()
    {
        var result = Evaluate(900, annual: false);

        Assert.True(Option(result, "enterprise").AnnualBillingOnly);
        Assert.False(Option(result, "basic").AnnualBillingOnly);

        // Priced at its annual rate on the monthly cycle, because that is the only rate there is —
        // dropping it out of the comparison would leave the top of the ladder blank.
        Assert.Equal(2_999.95m, Option(result, "enterprise").Subscription);
    }

    [Fact]
    public void Money_is_reported_in_cents_because_a_bill_has_two_decimal_places()
    {
        var result = Evaluate(937, plan: "none", monthlySales: 3_333.33m);

        Assert.All(result.Options, o =>
        {
            Assert.Equal(Math.Round(o.MonthlyCost, 2), o.MonthlyCost);
            Assert.Equal(Math.Round(o.AnnualCost, 2), o.AnnualCost);
        });
        Assert.Equal(Math.Round(result.MonthlySaving, 2), result.MonthlySaving);
    }

    [Fact]
    public void The_detail_line_prints_both_sides_of_the_switch_and_not_only_the_winner()
    {
        // A recommendation with only the recommended number on it is one the seller cannot check
        // against their own statement, which is the first thing they will want to do.
        var detail = Evaluate(900, plan: "none").Detail;

        Assert.Contains("$227.50", detail, StringComparison.Ordinal);
        Assert.Contains("$21.95", detail, StringComparison.Ordinal);
        Assert.Contains("Basic Store", detail, StringComparison.Ordinal);

        // "No store" is a tier here and it is the one the seller is on more often than any other,
        // so both halves of the sentence have to read as English when the answer is "nothing".
        Assert.Contains("You have no Store subscription, which covers 250 listings a month",
            detail, StringComparison.Ordinal);
        Assert.Contains("The Basic Store is $21.95 a month", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void And_reads_as_English_the_other_way_round_too_when_the_answer_is_to_drop_the_store()
    {
        var detail = Evaluate(214, plan: "basic").Detail;

        Assert.Contains("You are on the Basic Store at $21.95 a month", detail, StringComparison.Ordinal);
        Assert.Contains("Dropping the subscription leaves you the 250 free listings",
            detail, StringComparison.Ordinal);
        Assert.Contains("enough for all 214 of yours", detail, StringComparison.Ordinal);
    }
}
