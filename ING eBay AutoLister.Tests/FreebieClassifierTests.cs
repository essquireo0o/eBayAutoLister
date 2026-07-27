using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;
using Xunit;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The freebie finder is almost entirely refusal, so almost all of these pin a <c>null</c>.
///
/// A $0 cost basis makes ROI unbounded, which means anything this classifier lets through lands at
/// the very top of a profit ranking. There is no board in this app where a wrong answer is more
/// visible, so the tests that matter most are the ones proving something was thrown away.
///
/// Every title below marked "live" was pulled from craigslist or Slickdeals during the session this
/// feature was built, not invented.
/// </summary>
public class FreebieClassifierTests
{
    private static readonly DateTime Now = new(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);

    private static FreebieDetails? Classify(string title, decimal? price = null, bool freeBoard = false) =>
        FreebieClassifier.Classify(title, prose: null, price, freeBoard, Now);

    // ── "Free" attached to something other than the item ─────────────────────────────────────────

    [Theory]
    // The single commonest phrase on any deal feed, and the one that would fabricate the most
    // goldmines: a full-price product with free postage is not a free product.
    [InlineData("Sony WH-1000XM5 Headphones + Free Shipping")]
    [InlineData("Ktaxon 11500 BTU Mini Split System, ships free")]            // live
    [InlineData("Black + Decker Dustblaster Cordless Vacuum + Free Ship to Store")]  // live
    [InlineData("Bella PRO 8-Quart Air Fryer, free 2-day delivery")]
    public void Free_shipping_is_not_a_free_item(string title)
    {
        Assert.Null(Classify(title, price: 249.99m));
    }

    [Theory]
    [InlineData("Home Depot ONE+ 18V Starter Kit + Free Bonus Gift")]         // live
    [InlineData("medicube Toner Pads + FREE Gift WYB 2")]                     // live
    [InlineData("Select Subway Buy One Footlong Get One Free")]               // live
    [InlineData("Nike Socks, free item with purchase")]
    public void Free_with_purchase_is_not_a_free_item(string title)
    {
        Assert.Null(Classify(title, price: 99m));
    }

    [Theory]
    [InlineData("One Month Free Target Circle 360")]                          // live
    [InlineData("Free trial of Paramount+")]
    [InlineData("Verizon Wireless - daily free deals free $$ - Customer only")]  // live
    [InlineData("Round Trip Seattle to Beijing $674 with 1 Free Checked Bag")]   // live
    public void Free_time_access_or_credit_is_not_an_object(string title)
    {
        Assert.Null(Classify(title));
    }

    /// <remarks>
    /// Found live: "2 free 1 gallon square BPA-free jugs" was read as free (correct — it is on the
    /// free board) but ALSO had the word cut out of its own title, producing "BPA- jugs". A product
    /// nothing will ever comp. The same bug hit oil-free face wash and aluminum-free deodorant.
    /// </remarks>
    [Theory]
    [InlineData("Neutrogena Oil-Free Pink Grapefruit Acne Face Wash", "Neutrogena Oil-Free Pink Grapefruit Acne Face Wash")]
    [InlineData("2 1 gallon square BPA-free jugs", "2 1 gallon square BPA-free jugs")]
    [InlineData("Schmidt's Aluminum-Free Vegan Deodorant", "Schmidt's Aluminum-Free Vegan Deodorant")]
    public void Hyphenated_compounds_keep_their_own_word(string title, string expected)
    {
        Assert.Equal(expected, FreebieClassifier.CleanTitle(title));
    }

    [Fact]
    public void Hyphenated_free_is_not_read_as_a_free_item()
    {
        // A $12 sugar-free syrup is a $12 syrup.
        Assert.Null(Classify("Torani Sugar-Free Vanilla Syrup 750ml", price: 12m));
    }

    // ── Free, but not a single sellable object ───────────────────────────────────────────────────

    [Theory]
    [InlineData("Cleaning out garage and have a lot of free stuff")]          // live
    [InlineData("Free stuff pile - delwood st")]                              // live
    [InlineData("FREE Curb Alert - Help Yourself! First Come, First Served")] // live
    [InlineData("Free")]                                                      // live
    public void A_pile_is_not_a_product(string title)
    {
        Assert.Null(Classify(title, freeBoard: true));
    }

    [Theory]
    [InlineData("Free firewood pine tree wood")]                              // live
    [InlineData("FREE Drywall - Must take ALL")]                              // live
    [InlineData("Free scrap steel/metal")]                                    // live
    [InlineData("FREE WOODEN PALLETS")]                                       // live
    [InlineData("FREE moving boxes")]                                         // live
    [InlineData("Free Dirt")]                                                 // live
    [InlineData("Railroad Ties. FREE")]
    public void Bulk_materials_have_no_resale_comp(string title)
    {
        Assert.Null(Classify(title, freeBoard: true));
    }

    [Fact]
    public void A_misspelled_refusal_word_gets_through_and_that_is_the_accepted_limit()
    {
        // The live post said "Railroad Tries." A phrase list cannot catch typos, and this is the
        // honest failure mode: the row survives, finds no sold comp for a misspelling, and is
        // reported as "no sold history" rather than becoming a fabricated goldmine.
        Assert.NotNull(Classify("Railroad Tries. FREE", freeBoard: true));
    }

    [Theory]
    [InlineData("Leghorn Rooster")]                                           // live
    [InlineData("FREE FOOD")]                                                 // live
    [InlineData("Free Hand Sanitizer")]                                       // live
    [InlineData("FREE SODA/SNACK VENDING MACHINES PLACED ON YOUR BUSINESS")]  // live
    [InlineData("Free Kindle Books - Kids' Books, SAT/GMAT & Fiction")]       // live
    [InlineData("PS Plus July Freebies: Call of Duty, For the King II")]      // live
    public void Things_that_are_not_flippable_stock_are_refused(string title)
    {
        Assert.Null(Classify(title, freeBoard: true));
    }

    [Fact]
    public void Replicas_are_refused_however_free()
    {
        // Live. Priced against genuine sold comps this reads as the best flip on the board.
        Assert.Null(Classify("Free knock off yeezy", freeBoard: true));
    }

    [Fact]
    public void A_sweepstakes_is_not_a_freebie_but_giving_something_away_is()
    {
        Assert.Null(Classify("Enter to win a PS5 - sweepstakes", freeBoard: true));

        // "Giveaway" on a free board means "I am giving this away", not "enter a draw" — the two
        // are told apart on the wording rather than on the word. Live title.
        Assert.NotNull(Classify("Giveaway instruments - Charles Walter Piano", freeBoard: true));
    }

    // ── Free because it is broken ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Free 75'' flatscreen Hisense TV - Damaged panel/screen")]    // live
    [InlineData("Free stove - good for parts")]                              // live
    [InlineData("FREE Repair or Project 41\" Round Farmhouse Table")]        // live
    [InlineData("BROKEN REFRIDGERATOR")]                                     // live
    [InlineData("Free Scrap hot water heater.")]                             // live
    public void Free_because_broken_is_refused(string title)
    {
        // The sold history this would be priced against is history for items that work — and a
        // damaged 75" television against working-TV comps is the most expensive row this app
        // could print.
        Assert.Null(Classify(title, freeBoard: true));
    }

    // ── What the free board is actually for ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("Oak wall unit")]                                            // live
    [InlineData("DEWALT 20V MAX Circular Saw, Cordless Sidewinder Style")]   // live
    [InlineData("CRAFTSMAN 26.5 in. 5 drawer Steel Ball-Bearing Tool Center")] // live
    [InlineData("Kodak Ektagraphic Model E Slide projector")]                // live
    public void A_free_board_post_is_free_even_when_it_never_says_so(string title)
    {
        // The free-stuff category is free by construction. Requiring the word would throw away most
        // of the best supply on this feature — the majority of those 186 live posts never said it.
        var details = Classify(title, freeBoard: true);

        Assert.NotNull(details);
        Assert.Equal(FreebieKinds.Free, details!.Kind);
        Assert.True(details.IsPickup);
        Assert.Equal(FreebieUrgency.FirstCome, details.Urgency);
    }

    [Fact]
    public void A_priced_post_on_a_free_board_is_not_a_freebie()
    {
        Assert.Null(Classify("Dining table", price: 120m, freeBoard: true));
    }

    [Fact]
    public void The_word_free_is_stripped_so_the_comp_lookup_sees_the_product()
    {
        // Live, and both halves matter: "free" must go, and the "AND" it was joined to must go with
        // it, or the comp matcher is handed a product name ending in a conjunction.
        Assert.Equal("COUCH - BIG AND COMFORTABLE", FreebieClassifier.CleanTitle("COUCH - BIG AND COMFORTABLE AND FREE"));
        Assert.Equal("Kenmore Stove", FreebieClassifier.CleanTitle("Free Kenmore Stove"));
        Assert.Equal("Treadmill", FreebieClassifier.CleanTitle("FREE Treadmill"));
    }

    [Fact]
    public void A_price_that_describes_the_product_is_not_stripped_from_the_title()
    {
        // Live: "32 Degrees Underwear Under $10 Sale" came back as "32 Degrees Under Sale" from a
        // pattern that deleted every dollar figure. The price was load-bearing.
        Assert.Contains("Under $10", FreebieClassifier.CleanTitle("32 Degrees Underwear Under $10 Sale"));
    }

    // ── Bulky ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Freight_sized_free_stock_is_flagged_rather_than_refused()
    {
        // A free treadmill is real money to a seller who can move it — but the eBay comps behind
        // its price were set by people who did not post it in a box.
        var details = Classify("Proform Heavy Duty Treadmill", freeBoard: true);

        Assert.NotNull(details);
        Assert.True(details!.IsBulky);
        Assert.NotNull(FreebiePricer.CapReason(details));
    }

    // ── Free after rebate: decided by arithmetic, not wording ────────────────────────────────────

    [Fact]
    public void A_rebate_smaller_than_the_price_is_not_free()
    {
        // Live. It says "after rebate" in exactly the words a genuine freebie does, and it is not
        // free by $39.99. Only the subtraction can tell them apart.
        Assert.Null(Classify("Phanteks XT Pro Silent Mid-Tower ATX Case $49.99 after $10 Rebate", price: 49.99m));
    }

    [Fact]
    public void A_rebate_covering_the_price_is_free_after_rebate()
    {
        var details = Classify("Logitech Mouse $24.99, free after $24.99 mail-in rebate", price: 24.99m);

        Assert.NotNull(details);
        Assert.Equal(FreebieKinds.FreeAfterRebate, details!.Kind);
        Assert.Equal(24.99m, details.RebateAmount);
        Assert.Equal(24.99m, details.ListPrice);
    }

    [Fact]
    public void A_rebate_with_no_price_to_subtract_it_from_is_refused()
    {
        // Nothing here says whether this ends at $0 or at $40.
        Assert.Null(Classify("Great deal, free after rebate", price: null));
    }

    [Fact]
    public void A_rebate_that_fronts_too_much_money_is_not_a_freebie()
    {
        // Fronting $40 for six weeks is a freebie with a wait. Fronting $600 is a loan.
        Assert.Null(Classify("4K Monitor $599, free after $599 rebate", price: 599m));
    }

    [Fact]
    public void An_app_paid_rebate_is_recorded_as_such()
    {
        // Live wording. It matters: the money comes back in days rather than two months.
        var details = Classify(
            "Free So Good So You sparkling energy drink $3.99 (after rebate, through Venmo)", price: 3.99m);

        Assert.NotNull(details);
        Assert.Equal(FreebieKinds.FreeAfterRebate, details!.Kind);
        Assert.Equal("Venmo", details.RebateVia, ignoreCase: true);
    }

    // ── Free after coupon, and near free ─────────────────────────────────────────────────────────

    [Fact]
    public void Free_with_a_code_says_the_code_is_required()
    {
        var details = Classify("Anker USB-C Cable, free after coupon");

        Assert.NotNull(details);
        Assert.Equal(FreebieKinds.FreeAfterCoupon, details!.Kind);
        Assert.True(details.RequiresCoupon);
    }

    [Fact]
    public void A_small_real_price_is_near_free_and_a_large_one_is_not()
    {
        Assert.Equal(FreebieKinds.NearFree, Classify("Fender Celluloid Picks 12-pack", price: 3.99m)?.Kind);
        Assert.Null(Classify("Anker Power Bank", price: 19.99m));
    }

    // ── The clock ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_offer_whose_stated_date_has_passed_is_refused()
    {
        // Live titles, both already dead on the day they were pulled. A board that ranks one is
        // telling the seller to go and buy something that no longer exists.
        Assert.Null(Classify("Free Betty Crocker Cake Mix, via online submission ex 7/11", price: 3.99m));
        Assert.Null(Classify("$10 Publix rebate, free General Mills products exp 7/19/26", price: 4m));
    }

    [Fact]
    public void A_year_less_date_still_ahead_is_kept_and_dated()
    {
        var (expires, text) = FreebieClassifier.ReadExpiry("Free sample, exp 8/15", Now);

        Assert.Equal(new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc), expires);
        Assert.Contains("8/15", text);
    }

    [Fact]
    public void A_year_less_date_just_behind_us_rolls_to_next_year_only_when_it_is_far_behind()
    {
        // Two weeks ago: this year's, and therefore expired.
        var (recent, _) = FreebieClassifier.ReadExpiry("exp 7/11", Now);
        Assert.Equal(2026, recent!.Value.Year);

        // Six months "ago" read in July is next January's, not last January's.
        var (rolled, _) = FreebieClassifier.ReadExpiry("exp 1/5", Now);
        Assert.Equal(2027, rolled!.Value.Year);
    }

    [Fact]
    public void A_deadline_stated_in_words_is_read_as_today()
    {
        Assert.Equal(FreebieUrgency.Today, Classify("Free tote bag, today only")?.Urgency);
        Assert.Equal(FreebieUrgency.Today, Classify("MAY 4th ONLY - free keyboard")?.Urgency);   // live
    }

    [Fact]
    public void A_date_inside_the_week_is_this_week_and_further_out_is_not()
    {
        Assert.Equal(FreebieUrgency.ThisWeek, Classify("Free mug, exp 7/31")?.Urgency);
        Assert.Equal(FreebieUrgency.Unknown, Classify("Free mug, exp 9/30")?.Urgency);
    }

    [Fact]
    public void An_impossible_date_is_no_deadline_rather_than_a_crash()
    {
        var (expires, _) = FreebieClassifier.ReadExpiry("exp 2/30", Now);
        Assert.Null(expires);
    }

    // ── Delivery ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_online_freebie_that_states_no_delivery_cost_says_so()
    {
        var unstated = Classify("Free Limited-Edition Shaker Cup");   // live
        Assert.NotNull(unstated);
        Assert.False(unstated!.DeliveryCostKnown);
        // On a $0 item the shipping IS the price, so this is the one caveat that must reach the row.
        Assert.NotNull(FreebiePricer.CapReason(unstated));

        var stated = Classify("Free Anker Cable + free shipping");
        Assert.NotNull(stated);
        Assert.True(stated!.DeliveryCostKnown);
        Assert.Null(FreebiePricer.CapReason(stated));
    }

    [Fact]
    public void A_blank_title_is_refused_rather_than_becoming_a_free_nothing()
    {
        Assert.Null(Classify("", freeBoard: true));
        Assert.Null(Classify("   ", freeBoard: true));
    }
}
