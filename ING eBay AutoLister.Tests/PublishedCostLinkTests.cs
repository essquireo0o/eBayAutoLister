using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The moment a publish succeeds is the only instant when both halves of the join exist: the SKU the
// seller's draft carried, and the listing ID eBay has just minted. Before it there is no listing;
// after it the screen that knew what the item cost has been closed.
//
// So this is where a lot won at 11pm stops being a cost nobody ever enters. What is pinned here is
// mostly what it REFUSES to do, because every one of those refusals is a way to write a wrong number
// into the table every profit figure in the app reads:
//
//   · it never guesses which deal — an exact SKU match or nothing;
//   · it never overwrites a cost the seller entered themselves;
//   · it never picks between two cards carrying one SKU;
//   · it never moves a card backwards: sold stays sold, dropped stays dropped;
//   · it never claims in words that something was recorded when it was not.
public class PublishedCostLinkTests
{
    private const string Sku = "WN-20260806-A1B2C3";
    private const string ListingId = "1234567890";

    // ── The join it exists to make ────────────────────────────────────────────

    /// <summary>
    /// The whole feature in one case: a lot bought on a live show, drafted under its SKU, published.
    /// The card learns which listing it became and what the item cost gets written — with nobody
    /// typing an eBay listing ID into a board at midnight.
    /// </summary>
    [Fact]
    public void A_bought_deal_carrying_this_sku_is_joined_to_the_listing_that_just_went_live()
    {
        var plan = PublishedCostLink.Decide(Sku, ListingId, [Bought()], []);

        Assert.Equal(PublishedCostLink.LinkOutcome.Link, plan.Outcome);
        Assert.True(plan.ShouldWrite);
        Assert.Equal(7, plan.DealId);
        Assert.Equal(Sku, plan.Sku);
    }

    /// <summary>Publishing IS what Listed means, so a card behind it moves up.</summary>
    [Fact]
    public void A_card_that_has_not_reached_listed_is_advanced_to_it()
    {
        Assert.True(PublishedCostLink.Decide(Sku, ListingId, [Bought(DealStages.Sourced)], []).AdvanceToListed);
        Assert.True(PublishedCostLink.Decide(Sku, ListingId, [Bought(DealStages.Bought)], []).AdvanceToListed);
    }

    /// <summary>
    /// A card already at Listed is where the seller put it, and one past it has been settled. An app
    /// that dragged a sold deal back to Listed would be rewriting the seller's own record of what
    /// happened — and the cost is still written either way, which is the part that matters.
    /// </summary>
    [Theory]
    [InlineData(DealStages.Listed)]
    [InlineData(DealStages.Sold)]
    [InlineData(DealStages.Dropped)]
    public void A_card_at_or_past_listed_is_left_where_the_seller_put_it(string stage)
    {
        var plan = PublishedCostLink.Decide(Sku, ListingId, [Bought(stage)], []);

        Assert.True(plan.ShouldWrite);
        Assert.False(plan.AdvanceToListed);
    }

    /// <summary>
    /// Re-publishing the same draft — a reconciled publish, or a seller pressing it twice — finds
    /// the deal already pointed at this listing. That is the same join, not a different one, so it
    /// is allowed through and the write below it is idempotent.
    /// </summary>
    [Fact]
    public void Publishing_the_same_draft_onto_the_same_listing_is_still_the_same_join()
    {
        var deal = Bought(DealStages.Listed);
        deal.ListingId = ListingId;

        Assert.True(PublishedCostLink.Decide(Sku, ListingId, [deal], []).ShouldWrite);
    }

    /// <summary>The listing's SKU has been through eBay's fence and the card's is whatever the
    /// seller typed. Both are cleaned before they are compared, so a card labelled with spaces
    /// still finds the listing that went out hyphenated.</summary>
    [Fact]
    public void A_hand_typed_sku_still_matches_the_one_that_reached_ebay()
    {
        var deal = Bought();
        deal.Sku = "WN 20260806 A1B2C3";

        var plan = PublishedCostLink.Decide("WN-20260806-A1B2C3", ListingId, [deal], []);

        Assert.Equal(PublishedCostLink.LinkOutcome.Link, plan.Outcome);
    }

    [Fact]
    public void Case_is_not_what_decides_whether_two_skus_are_the_same_item()
    {
        var deal = Bought();
        deal.Sku = "wn-20260806-a1b2c3";

        Assert.True(PublishedCostLink.Decide(Sku, ListingId, [deal], []).ShouldWrite);
    }

    // ── What it refuses ───────────────────────────────────────────────────────

    /// <summary>Most listings have no SKU, and that is not a problem to report — it is the ordinary
    /// case, and there is simply nothing to join on.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_listing_with_no_sku_of_its_own_joins_to_nothing_and_says_nothing(string? sku)
    {
        var plan = PublishedCostLink.Decide(sku, ListingId, [Bought()], []);

        Assert.Equal(PublishedCostLink.LinkOutcome.NoSku, plan.Outcome);
        Assert.False(plan.ShouldWrite);
        Assert.Equal("", PublishedCostLink.Say(plan));
    }

    /// <summary>A publish reconciled after a dropped connection can come back without an ID. Half a
    /// join is not a join: writing a cost basis with no listing ID against a SKU that eBay may or
    /// may not have accepted is a row nothing will ever match.</summary>
    [Fact]
    public void Without_a_listing_id_there_is_no_join_to_make()
    {
        var plan = PublishedCostLink.Decide(Sku, "  ", [Bought()], []);

        Assert.Equal(PublishedCostLink.LinkOutcome.NoListingId, plan.Outcome);
        Assert.False(plan.ShouldWrite);
    }

    /// <summary>
    /// The refusal that keeps the whole thing honest: no title matching, no "the most recent card",
    /// no nearest guess. A cost written against the wrong item is wrong silently, and every profit
    /// figure downstream inherits it.
    /// </summary>
    [Fact]
    public void A_sku_no_deal_carries_matches_nothing_rather_than_the_closest_card()
    {
        var other = Bought();
        other.Sku = "WN-20260805-ZZZZZZ";
        other.Title = "Bitmain Antminer S19j Pro 104TH";

        var plan = PublishedCostLink.Decide(Sku, ListingId, [other], []);

        Assert.Equal(PublishedCostLink.LinkOutcome.NoDeal, plan.Outcome);
        Assert.False(plan.ShouldWrite);
    }

    /// <summary>
    /// Two cards under one SKU means the SKU is not the key it is being used as. Taking the first is
    /// taking one arbitrarily — so it writes nothing and says which two facts are in conflict.
    /// </summary>
    [Fact]
    public void Two_deals_under_one_sku_are_reported_rather_than_chosen_between()
    {
        var a = Bought();
        var b = Bought();
        b.Id = 8;

        var plan = PublishedCostLink.Decide(Sku, ListingId, [a, b], []);

        Assert.Equal(PublishedCostLink.LinkOutcome.AmbiguousSku, plan.Outcome);
        Assert.False(plan.ShouldWrite);
        Assert.Contains("could not tell which cost", PublishedCostLink.Say(plan), StringComparison.Ordinal);
    }

    /// <summary>A card with no purchase price on it is a deal nobody has said they paid for. There
    /// is no cost to record, and the seller is told where to put one.</summary>
    [Fact]
    public void A_deal_with_no_purchase_price_has_no_cost_to_record()
    {
        var deal = Bought();
        deal.PurchasePrice = null;

        var plan = PublishedCostLink.Decide(Sku, ListingId, [deal], []);

        Assert.Equal(PublishedCostLink.LinkOutcome.NoPurchasePrice, plan.Outcome);
        Assert.False(plan.ShouldWrite);
        Assert.Contains("Apply what you paid", PublishedCostLink.Say(plan), StringComparison.Ordinal);
    }

    /// <summary>
    /// The card is already pointed at a live listing. Re-pointing it would move the cost off an item
    /// that may have already sold under it, which would make a settled profit figure wrong after the
    /// fact.
    /// </summary>
    [Fact]
    public void A_deal_already_joined_to_another_listing_is_left_alone()
    {
        var deal = Bought();
        deal.ListingId = "9999999999";

        var plan = PublishedCostLink.Decide(Sku, ListingId, [deal], []);

        Assert.Equal(PublishedCostLink.LinkOutcome.JoinedElsewhere, plan.Outcome);
        Assert.False(plan.ShouldWrite);
    }

    /// <summary>
    /// The seller typing what they paid is the most reliable input this app has. A bookkeeping
    /// convenience that overwrote it would be a bug that only surfaces as a wrong profit months
    /// later — so an existing cost basis stands, whichever key it was found by.
    /// </summary>
    [Fact]
    public void A_cost_the_seller_already_recorded_for_this_listing_is_never_overwritten()
    {
        var existing = new CostBasisEntry { ListingId = ListingId, UnitCost = 95m };

        var plan = PublishedCostLink.Decide(Sku, ListingId, [Bought()], [existing]);

        Assert.Equal(PublishedCostLink.LinkOutcome.AlreadyRecorded, plan.Outcome);
        Assert.False(plan.ShouldWrite);
    }

    /// <summary>Found by SKU, too — that fallback is exactly what keeps a relisted item's cost, so
    /// a relist must not be the thing that erases it.</summary>
    [Fact]
    public void A_cost_recorded_under_this_sku_survives_the_item_being_relisted()
    {
        var existing = new CostBasisEntry { ListingId = "an-older-listing", Sku = Sku, UnitCost = 95m };

        var plan = PublishedCostLink.Decide(Sku, ListingId, [Bought()], [existing]);

        Assert.Equal(PublishedCostLink.LinkOutcome.AlreadyRecorded, plan.Outcome);
    }

    [Fact]
    public void An_empty_board_is_not_an_error()
    {
        var plan = PublishedCostLink.Decide(Sku, ListingId, null, null);

        Assert.Equal(PublishedCostLink.LinkOutcome.NoDeal, plan.Outcome);
        Assert.False(plan.ShouldWrite);
    }

    // ── What it says ──────────────────────────────────────────────────────────

    /// <summary>
    /// The sentence is the seller's proof that the money was captured, so it names the SKU, says
    /// whether the card moved, and carries the cost table's own words about how many already-
    /// completed sales just became real profit.
    /// </summary>
    [Fact]
    public void The_success_sentence_names_the_sku_the_move_and_the_cost_tables_own_words()
    {
        var plan = PublishedCostLink.Decide(Sku, ListingId, [Bought()], []);

        var said = PublishedCostLink.Say(plan, "Recorded $131.60 as what this cost you.");

        Assert.Contains(Sku, said, StringComparison.Ordinal);
        Assert.Contains("moved to Listed", said, StringComparison.Ordinal);
        Assert.Contains("Recorded $131.60", said, StringComparison.Ordinal);
    }

    /// <summary>A card already at Listed did not move, and the sentence does not say it did.</summary>
    [Fact]
    public void A_card_that_did_not_move_is_not_said_to_have_moved()
    {
        var plan = PublishedCostLink.Decide(Sku, ListingId, [Bought(DealStages.Sold)], []);

        var said = PublishedCostLink.Say(plan, "Recorded $131.60 as what this cost you.");

        Assert.DoesNotContain("moved to Listed", said, StringComparison.Ordinal);
        Assert.Contains("Recorded $131.60", said, StringComparison.Ordinal);
    }

    /// <summary>The write can be refused after the decision is made — a locked table, a deleted
    /// card. The sentence still has to be a true one, so it never claims a figure it wasn't given.</summary>
    [Fact]
    public void With_no_word_from_the_cost_table_the_sentence_claims_no_figure()
    {
        var plan = PublishedCostLink.Decide(Sku, ListingId, [Bought()], []);

        var said = PublishedCostLink.Say(plan);

        Assert.Contains("now recorded against this listing", said, StringComparison.Ordinal);
        Assert.DoesNotContain("$", said, StringComparison.Ordinal);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static DealRecord Bought(string stage = DealStages.Bought) => new()
    {
        Id = 7,
        Stage = stage,
        Title = "Bitmain Antminer S19j Pro 104TH",
        Source = WonLotListing.DealSource,
        Sku = Sku,
        Quantity = 1,
        PurchasePrice = 120m,
        PurchaseExtraCost = 11.60m,
        BoughtUtc = new DateTimeOffset(2026, 8, 6, 23, 30, 0, TimeSpan.Zero),
    };
}
