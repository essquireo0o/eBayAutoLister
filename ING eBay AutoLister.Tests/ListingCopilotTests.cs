using ING_eBay_AutoLister.Services;
using Xunit;

namespace ING_eBay_AutoLister.Tests;

// The Copilot's whole safety story is that the plan is computed without touching anything, so
// these tests are the plan's correctness. The apply step can only ever do what the plan says.
public class ListingCopilotTests
{
    // ── Titles ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_title_that_is_already_good_is_left_alone()
    {
        var issues = ListingCopilot.AuditTitle(
            "Dewalt DCD791 20V Max XR Brushless Cordless Drill Driver Tool Only Bare");
        Assert.Empty(issues);
    }

    [Fact]
    public void An_over_length_title_says_how_far_over_it_is()
    {
        var t = new string('a', 95);
        var issue = Assert.Single(ListingCopilot.AuditTitle(t), i => i.Code == "title_too_long");
        Assert.Contains("95", issue.Summary);
        Assert.Contains("15", issue.Summary);   // 95 - 80
    }

    [Fact]
    public void A_short_title_is_reported_as_unused_keyword_space()
    {
        var issue = Assert.Single(ListingCopilot.AuditTitle("Drill"), i => i.Code == "title_short");
        Assert.Contains("5 of 80", issue.Summary);
        Assert.Contains("75", issue.WhyItCosts);   // 80 - 5 characters going unused
    }

    [Theory]
    [InlineData("L@@K rare drill")]
    [InlineData("Drill FREE SHIPPING")]
    [InlineData("must see drill!!!")]
    public void Filler_words_are_flagged_because_nobody_searches_them(string title)
    {
        Assert.Contains(ListingCopilot.AuditTitle(title), i => i.Code == "title_filler");
    }

    [Fact]
    public void Tidying_removes_filler_without_destroying_the_real_words()
    {
        var tidied = ListingCopilot.TidyTitle("L@@K!! Dewalt DCD791 Cordless Drill FREE SHIPPING");
        Assert.Contains("Dewalt DCD791", tidied);
        Assert.Contains("Cordless Drill", tidied);
        Assert.DoesNotContain("L@@K", tidied, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FREE SHIPPING", tidied, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("  ", tidied);
    }

    [Fact]
    public void Tidying_never_returns_a_title_ebay_would_reject()
    {
        var tidied = ListingCopilot.TidyTitle(string.Join(" ", Enumerable.Repeat("keyword", 40)));
        Assert.True(tidied.Length <= ListingCopilot.MaxTitleLength);
    }

    [Fact]
    public void Truncation_lands_on_a_word_boundary_rather_than_mid_word()
    {
        // A title ending "...Cordl" looks broken, and the fragment is not a keyword anyone searches.
        var tidied = ListingCopilot.TidyTitle(
            "Dewalt DCD791 20V Max XR Brushless Cordless Drill Driver Kit With Battery And Charger Included");
        Assert.True(tidied.Length <= ListingCopilot.MaxTitleLength);
        Assert.False(tidied.EndsWith(' '));
        Assert.DoesNotContain("  ", tidied);
    }

    [Fact]
    public void An_empty_title_is_the_only_issue_reported_for_an_empty_title()
    {
        // Reporting "too short" as well would bury the one thing that matters.
        var issue = Assert.Single(ListingCopilot.AuditTitle("   "));
        Assert.Equal("title_missing", issue.Code);
    }

    // ── Categories ────────────────────────────────────────────────────────────

    [Fact]
    public void A_missing_category_is_reported()
    {
        Assert.Contains(ListingCopilot.AuditCategory("", "Anything"), i => i.Code == "category_missing");
    }

    [Fact]
    public void Category_data_we_never_fetched_is_not_reported_as_a_missing_category()
    {
        // eBay's bulk listing call omits the category entirely. Calling that "no category" invents
        // a fault on every listing in the account - confident and wrong, which is worse than quiet.
        var issue = Assert.Single(ListingCopilot.AuditCategory("", ""));
        Assert.Equal("category_unknown", issue.Code);
        Assert.DoesNotContain(ListingCopilot.AuditCategory(null, null), i => i.Code == "category_missing");
    }

    [Fact]
    public void A_catch_all_category_is_reported_because_it_carries_no_buyer_filters()
    {
        Assert.Contains(ListingCopilot.AuditCategory("625", "Cameras & Photo > Other"),
            i => i.Code == "category_catchall");
    }

    [Fact]
    public void A_real_leaf_category_is_left_alone()
    {
        Assert.Empty(ListingCopilot.AuditCategory("31388", "Cameras & Photo > Digital Cameras"));
    }

    [Fact]
    public void A_category_that_was_filled_in_stops_being_reported_as_unknown()
    {
        // Once the scan's per-item lookup fills a real category in, the audit must switch from the
        // "not loaded" gap to judging the placement — the whole point of fetching the category is
        // that this listing no longer counts toward categoryUnknown.
        var blank = ListingCopilot.AuditCategory("", "");
        Assert.Contains(blank, i => i.Code == "category_unknown");

        var filled = ListingCopilot.AuditCategory("31388", "Cameras & Photo > Digital Cameras");
        Assert.DoesNotContain(filled, i => i.Code == "category_unknown");
        Assert.Empty(filled);
    }

    // ── Policies ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("FREE SHIP!!", "Free Ship")]
    [InlineData("copy of Ground Shipping", "Ground Shipping")]
    [InlineData("copy of copy of ground shipping", "Ground Shipping")]
    [InlineData("ship  2", "Ship 2")]
    [InlineData("Ground Shipping (3)", "Ground Shipping 3")]
    public void Policy_names_canonicalise_to_one_predictable_shape(string raw, string expected)
        => Assert.Equal(expected, ListingCopilot.CanonicalPolicyName(raw));

    [Theory]
    [InlineData("UPS Ground - $19.99 - 2 Day Handle", "UPS Ground - $19.99 - 2 Day Handle")]
    [InlineData("ups ground", "UPS Ground")]
    [InlineData("usps first class", "USPS First Class")]
    [InlineData("fedex 2 day", "FedEx 2 Day")]
    [InlineData("Lot of 100 - Miners - UPS Ground", "Lot of 100 - Miners - UPS Ground")]
    public void Carrier_acronyms_survive_the_rename(string raw, string expected)
    {
        // Found against the real account: lowercasing before title-casing turned "UPS Ground" into
        // "Ups Ground" - a rename that left the account worse than it started.
        Assert.Equal(expected, ListingCopilot.CanonicalPolicyName(raw));
    }

    [Fact]
    public void A_policy_already_named_with_an_acronym_is_not_in_the_plan_at_all()
    {
        Assert.Empty(ListingCopilot.PlanPolicyRenames([new("1", "UPS Ground - $19.99 - 2 Day Handle")]));
    }

    [Theory]
    [InlineData("UPS Ground - $34.99 - 3 Day Handle + Intl $329.99")]
    [InlineData("UPS Ground - $49.99 - 1 Day Handle + Intl $350")]
    [InlineData("UPS Ground - $299.99 - 3 Day Handle + Intl $1,299 (Lot 5)")]
    [InlineData("Ground - 2 Day Handle - $19.99")]
    public void A_price_at_the_end_of_a_name_is_never_mistaken_for_a_duplicate_marker(string raw)
    {
        // All four are real policy names from the live account. Treating bare trailing digits as a
        // copy index split "$329.99" into "$329. 99" and "$350" into "$ 350", and ate the closing
        // bracket of "(Lot 5)". A rename that mangles a price is worse than no rename at all.
        Assert.Equal(raw, ListingCopilot.CanonicalPolicyName(raw));
    }

    [Fact]
    public void A_parenthesised_copy_index_is_still_flattened()
    {
        Assert.Equal("Ground Shipping 3", ListingCopilot.CanonicalPolicyName("Ground Shipping (3)"));
    }

    [Theory]
    [InlineData("UPS SurePost - $29.99 - 2 Day Handle")]   // SurePost is a UPS product name
    [InlineData("PayPal123")]                              // brand intercap
    [InlineData("Lot of 100 - Miners - UPS Ground")]       // "of" stays lowercase mid-name
    public void A_name_that_is_already_deliberate_is_left_exactly_as_it_is(string raw)
    {
        // Every one of these is a real policy from the live account that an earlier version of
        // this code "tidied" into something worse: Surepost, Paypal123, "Lot Of 100", and an id
        // with its brackets stripped off.
        Assert.Equal(raw, ListingCopilot.CanonicalPolicyName(raw));
        Assert.Empty(ListingCopilot.PlanPolicyRenames([new("1", raw)]));
    }

    [Fact]
    public void An_ebay_policy_id_in_a_name_keeps_its_brackets()
    {
        // The words are all lowercase so title-casing them is fair tidying. The 12-digit id is
        // not a copy index, and flattening its brackets turns an identifier into loose digits.
        Assert.Equal("30 Days Money Back (280206111018)",
            ListingCopilot.CanonicalPolicyName("30 days money back (280206111018)"));
    }

    [Fact]
    public void The_names_that_are_genuinely_broken_are_still_fixed()
    {
        // Leaving deliberate names alone must not turn the whole feature into a no-op.
        Assert.Equal("Free Ship", ListingCopilot.CanonicalPolicyName("FREE SHIP!!"));
        Assert.Equal("Ground Shipping", ListingCopilot.CanonicalPolicyName("copy of ground shipping"));
        Assert.Equal("Ok", ListingCopilot.CanonicalPolicyName("ok"));
    }

    [Fact]
    public void A_policy_whose_name_is_already_canonical_is_not_in_the_plan()
    {
        var plan = ListingCopilot.PlanPolicyRenames([new("1", "Ground Shipping")]);
        Assert.Empty(plan);
    }

    [Fact]
    public void Two_policies_that_canonicalise_the_same_never_get_the_same_name()
    {
        // eBay rejects duplicate policy names, so a colliding plan would fail halfway through and
        // leave the account half-renamed.
        var plan = ListingCopilot.PlanPolicyRenames(
        [
            new("1", "FREE SHIP!!"),
            new("2", "free ship"),
            new("3", "Free  Ship"),
        ]);

        var names = plan.Select(p => p.ProposedName).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void The_plan_carries_the_id_so_the_apply_step_cannot_rename_the_wrong_policy()
    {
        var rename = Assert.Single(ListingCopilot.PlanPolicyRenames([new("pol-42", "FREE SHIP!!")]));
        Assert.Equal("pol-42", rename.PolicyId);
        Assert.Equal("FREE SHIP!!", rename.CurrentName);
        Assert.Equal("Free Ship", rename.ProposedName);
    }

    [Fact]
    public void An_empty_account_produces_an_empty_plan_rather_than_throwing()
    {
        Assert.Empty(ListingCopilot.PlanPolicyRenames([]));
        Assert.Equal("Unnamed", ListingCopilot.CanonicalPolicyName(null));
    }
}
