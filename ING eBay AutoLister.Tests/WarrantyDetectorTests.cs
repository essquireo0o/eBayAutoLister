using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;
using Xunit;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// What a listing actually says about cover, and — more often — what it doesn't.
///
/// The failure these guard against is one-directional and expensive: reading a warranty onto a
/// listing that has none puts a premium on a resale price and a green badge on a buy the seller
/// cannot return. So most of what follows is refusal, and the cases that look like cover but aren't
/// ("no longer under warranty", "extended warranty available") are pinned first.
/// </summary>
public class WarrantyDetectorTests
{
    // Fixed so a stated calendar date produces the same month count in ten years' time as it does
    // today. Anything measured relatively is written relatively.
    private static readonly DateTime Now = new(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);

    private static WarrantyDetails? Read(string text, string retailer = "") =>
        WarrantyDetector.Detect(text, detailText: null, retailer, Now);

    // ── Refusal ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_listing_that_says_the_warranty_ended_is_not_a_covered_listing()
    {
        // Contains the exact phrase "under warranty", which is the whole reason refusal runs first.
        var details = Read("MacBook Pro 16, no longer under warranty, works great");

        Assert.NotNull(details);
        Assert.Equal(WarrantyKinds.None, details!.Kind);
        Assert.Equal(0, details.MonthsRemaining);
        Assert.False(details.TransfersToBuyer);
    }

    [Theory]
    [InlineData("Dell XPS 15, no warranty, tested working")]
    [InlineData("Sony a7III body — warranty expired last year")]
    [InlineData("Antminer S19j Pro, out of warranty, runs cool")]
    [InlineData("iPhone 14 Pro — sold as-is, no returns")]
    [InlineData("Bose 700 headphones. All sales final.")]
    public void Stated_absence_of_cover_is_read_as_absence(string title)
    {
        Assert.Equal(WarrantyKinds.None, Read(title)?.Kind);
    }

    [Fact]
    public void A_plan_being_sold_is_not_a_plan_being_included()
    {
        // Every big-box listing in existence offers one of these. Read as cover, this feature would
        // put a warranty chip on the entire retail half of the board.
        Assert.Null(Read("Samsung 65\" QLED TV — 3-year protection plan available for purchase"));
        Assert.Null(Read("LG C4 OLED, add a 4 year extended warranty at checkout"));
    }

    [Fact]
    public void Boilerplate_as_is_on_a_classified_is_not_a_disclaimer()
    {
        // "As is" is the most common two words on any classifieds board and means nothing there. Only
        // the forms where the seller is actually disclaiming count — see WarrantySelectors.NoCover.
        Assert.Null(Read("Bitmain Antminer S19j Pro, selling as is, pickup only"));
    }

    [Fact]
    public void Silence_about_warranty_is_silence()
    {
        Assert.Null(Read("Milwaukee M18 drill kit with two batteries and charger"));
    }

    // ── Stated cover, dated ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_stated_end_date_is_taken_at_its_word()
    {
        var details = Read("MacBook Pro M3, still under manufacturer warranty until 3/2027");

        Assert.NotNull(details);
        Assert.Equal(WarrantyKinds.Manufacturer, details!.Kind);
        Assert.Equal(WarrantyEvidence.Stated, details.Evidence);
        // July 2026 to the end of March 2027.
        Assert.Equal(8, details.MonthsRemaining);
        Assert.Equal(new DateTime(2027, 3, 31, 0, 0, 0, DateTimeKind.Utc), details.ExpiresUtc);
        // Apple's cover is looked up by serial, so the reseller's buyer gets it too.
        Assert.True(details.TransfersToBuyer);
    }

    [Fact]
    public void A_month_and_year_expiry_is_not_misread_as_a_day_and_month()
    {
        // "3/2027" matched day-first reads as 3/20 — three weeks away instead of eight months. The
        // whole feature turns on this one alternation being ordered correctly.
        Assert.Equal(8, Read("iPad Pro, warranty until 3/2027")?.MonthsRemaining);
    }

    [Fact]
    public void A_year_less_expiry_is_read_as_the_next_one_to_come()
    {
        // A warranty end date is in the future by definition, so "3/15" seen in July is next March.
        var details = Read("Dell XPS 13, warranty good through 3/15");

        Assert.Equal(2027, details?.ExpiresUtc?.Year);
        Assert.Equal(7, details?.MonthsRemaining);
    }

    [Fact]
    public void A_term_plus_a_purchase_date_dates_the_cover()
    {
        var details = Read("DeWalt DCD999 hammer drill, 3 year warranty, bought March 2025");

        Assert.NotNull(details);
        Assert.Equal(36, details!.TermMonths);
        // Bought 1 Mar 2025, cover to 1 Mar 2028, read on 27 Jul 2026.
        Assert.Equal(19, details.MonthsRemaining);
        // Real cover for the person buying it — and DeWalt's terms name the original purchaser, so
        // it is worth nothing to whoever they sell it to.
        Assert.False(details.TransfersToBuyer);
    }

    [Fact]
    public void A_term_with_no_start_date_is_a_length_and_not_a_remainder()
    {
        // "1 year warranty" on a used phone says how long the cover ran, not how much is left. The
        // difference between those two is the entire feature, so this stays null rather than
        // becoming twelve months of imaginary protection.
        var details = Read("iPhone 15 Pro 256GB, 1 year manufacturer warranty");

        Assert.NotNull(details);
        Assert.Equal(WarrantyEvidence.Stated, details!.Evidence);
        Assert.Equal(12, details.TermMonths);
        Assert.Null(details.MonthsRemaining);
    }

    [Fact]
    public void A_bare_term_in_front_of_the_word_is_still_a_stated_warranty()
    {
        // "90 day warranty" has no verb in front of it for the main pattern to hook onto, and it is
        // how every shop on a classifieds board states cover.
        var details = Read("Refurbished ThinkPad T14, 90 day warranty");

        Assert.NotNull(details);
        Assert.Equal(WarrantyEvidence.Stated, details!.Evidence);
        Assert.Equal(3, details.TermMonths);
    }

    // ── Who is on the hook ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_sellers_own_guarantee_is_told_apart_from_the_factorys()
    {
        var details = Read("Antminer S19j Pro, tested and hashing — 30 day warranty from me");

        Assert.NotNull(details);
        Assert.Equal(WarrantyKinds.Seller, details!.Kind);
        // The promise was made to the person buying it here. It does not follow the item onward.
        Assert.False(details.TransfersToBuyer);
        // And it starts when this buyer buys it, so the whole term is genuinely ahead of them.
        Assert.Equal(1, details.MonthsRemaining);
    }

    [Fact]
    public void A_named_protection_plan_is_its_own_kind()
    {
        var details = Read("iPhone 15 Pro Max, AppleCare+ until 11/2027");

        Assert.NotNull(details);
        Assert.Equal(WarrantyKinds.Extended, details!.Kind);
        Assert.Equal(WarrantyEvidence.Stated, details.Evidence);
        Assert.Equal(16, details.MonthsRemaining);
    }

    [Fact]
    public void A_refurbishment_programme_carries_its_published_terms()
    {
        var details = Read("Bose QuietComfort Ultra Headphones", retailer: "Amazon Renewed");

        Assert.NotNull(details);
        Assert.Equal(WarrantyKinds.Refurbisher, details!.Kind);
        Assert.Equal(WarrantyEvidence.Program, details.Evidence);
        Assert.Equal("Amazon Renewed", details.ProgramLabel);
        Assert.Equal(3, details.MonthsRemaining);
        // Amazon's guarantee is to Amazon's own purchaser, so there is nothing to advertise onward.
        Assert.False(details.TransfersToBuyer);
    }

    [Fact]
    public void Apples_refurbished_cover_transfers_where_a_generic_programmes_does_not()
    {
        Assert.True(Read("Apple Certified Refurbished MacBook Air M2")?.TransfersToBuyer);
        Assert.False(Read("Certified Refurbished Roku Ultra")?.TransfersToBuyer);
    }

    [Fact]
    public void The_listings_own_words_beat_the_catalogue_on_transferability()
    {
        // Said either way, the seller's statement wins over anything this app assumed.
        Assert.True(Read("DeWalt DCD999, under factory warranty, fully transferable")?.TransfersToBuyer);
        Assert.False(Read("MacBook Pro M3, under manufacturer warranty — non-transferable")?.TransfersToBuyer);
    }

    // ── Estimates, which are labelled as such ────────────────────────────────────────────────────

    [Fact]
    public void An_unopened_box_gets_the_brands_full_term_as_an_estimate()
    {
        var details = Read("Dyson V15 Detect, brand new in box, never opened");

        Assert.NotNull(details);
        Assert.Equal(WarrantyEvidence.Estimated, details!.Evidence);
        Assert.Equal(24, details.MonthsRemaining);
        // The label says so out loud, because a seller glancing at a board has to be able to tell
        // a stated warranty from one this app worked out.
        Assert.Contains("estimated", details.KindLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_purchase_date_in_words_ages_the_brands_cover()
    {
        var details = Read("Sony WH-1000XM5 headphones, bought 3 months ago, barely used");

        Assert.NotNull(details);
        Assert.Equal(WarrantyEvidence.Estimated, details!.Evidence);
        // Sony runs twelve months and three of them are gone.
        Assert.Equal(9, details.MonthsRemaining);
    }

    [Fact]
    public void An_estimate_with_nothing_to_estimate_from_is_dropped()
    {
        // "Refurbished" and a brand this app knows, and not one word about when anything happened.
        // A chip that says only "we looked" is worse than no chip.
        Assert.Null(Read("Used Garmin Fenix 7, good condition, works perfectly"));
    }

    [Fact]
    public void An_unknown_brand_gets_no_invented_term()
    {
        Assert.Null(Read("Zephyrtech ZT-900 pressure washer, bought 2 months ago"));
    }

    [Fact]
    public void An_estimate_that_the_cover_has_run_out_is_not_worth_a_chip()
    {
        // Sony runs twelve months and this is three years old. A guess that something ISN'T there
        // would fire on half a classifieds board and tell the seller nothing they can act on — the
        // as-is warning exists for the case where somebody actually said it.
        Assert.Null(Read("Sony WH-1000XM4 headphones, bought 3 years ago, still great"));
    }

    [Fact]
    public void Cover_that_has_already_run_out_reads_as_zero_rather_than_as_cover()
    {
        var details = Read("Samsung 65\" QLED, warranty until 2/2025");

        Assert.NotNull(details);
        Assert.Equal(0, details!.MonthsRemaining);
    }

    // ── The body text, not just the title ────────────────────────────────────────────────────────

    [Fact]
    public void The_warranty_is_usually_in_the_body_rather_than_the_title()
    {
        // Which is the whole reason LocalSupplyListing carries DetailText: no classifieds title has
        // room for this, and every classifieds body says it.
        var details = WarrantyDetector.Detect(
            "MacBook Air M2 13 inch 256GB",
            "Selling my MacBook Air, excellent condition. Still under Apple warranty until 5/2027, " +
            "I have the original receipt.",
            retailer: "", Now);

        Assert.NotNull(details);
        Assert.Equal(10, details!.MonthsRemaining);
        Assert.True(details.HasProofOfPurchase);
    }
}
