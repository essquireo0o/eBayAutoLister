using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// Price Position is the only board in this app that recommends a price CUT on stock the seller
// already owns, and a cut is money that leaves and does not come back. Every failure mode here is
// therefore the same shape: something that is not really competition gets counted, the board reports
// a premium that was never real, and the seller marks down a perfectly good listing to chase it.
//
// So what is pinned below is mostly the refusals — the rules that make the board's numbers smaller
// and its verdicts fewer. A repair-service listing at $1, a lot of ten, an auction mid-bid, a
// broken one at a fifth of the price, a rival whose shipping is not stated: each of those, counted,
// is a markdown recommended on a listing that did not need one. They are also exactly the kind of
// rule a later tidy-up deletes as "over-engineering", which is why each has a test that fails on
// exactly its removal.
public class PricePositionAnalyzerTests
{
    private static readonly DateTime Now = new(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

    private static readonly FeeProfile Fees = new()
    {
        EbayFinalValueFeePercent = 13.25m,
        EbayFinalValueFeeFixed = 0.40m,
    };

    private static PricePositionAnalyzer Analyzer() =>
        new(new ProductNormalizer(new ProductIdentityExtractor()), new NetProceedsCalculator(new ProfitCalculator()));

    private static EbayListingSummary Mine(
        decimal price = 200m, string title = "Bitmain Antminer S19 95TH Bitcoin Miner",
        decimal shipping = 0m, bool shippingKnown = true, int watchers = 0, int views = 500,
        int daysListed = 60, string condition = "Used", int quantity = 1) => new()
    {
        ListingId = "MINE-1",
        Sku = "SKU-1",
        Title = title,
        Price = price,
        Quantity = quantity,
        Condition = condition,
        WatchCount = watchers,
        HitCount = views,
        ShippingCost = shipping,
        ShippingCostKnown = shippingKnown,
        ListingUrl = "https://www.ebay.com/itm/MINE-1",
        StartTimeUtc = Now.AddDays(-daysListed),
    };

    private static EbayOpportunityItem Rival(
        decimal price, string title = "Bitmain Antminer S19 95TH Bitcoin Miner", decimal shipping = 0m,
        bool shippingStated = true, string id = "", string condition = "Used",
        string buyingOption = "FIXED_PRICE") => new()
    {
        ItemId = string.IsNullOrEmpty(id) ? $"R{price:0}{title.Length}{shipping:0}" : id,
        Title = title,
        Price = price,
        ShippingCost = shipping,
        ShippingStated = shippingStated,
        Condition = condition,
        BuyingOption = buyingOption,
        SellerUsername = "someone",
        SellerFeedbackScore = 400,
        Url = "https://www.ebay.com/itm/rival",
    };

    private static CostBasisEntry Cost(decimal unitCost) =>
        new() { ListingId = "MINE-1", UnitCost = unitCost };

    private static PricePositionRow Build(
        EbayListingSummary mine, IReadOnlyList<EbayOpportunityItem> rivals,
        CostBasisEntry? cost = null, bool viewsReported = true) =>
        Analyzer().Build(mine, rivals, cost, Fees, Now, viewsReported);

    // ── The comparison itself ────────────────────────────────────────────────────────────────

    [Fact]
    public void The_ranking_is_on_delivered_price_because_that_is_what_the_buyer_pays()
    {
        // $180 + $30 shipping is $210 delivered, and it is behind three listings at $190 that ship
        // free — even though every one of them has a HIGHER asking price. eBay's cheapest-first sort
        // orders on price plus shipping, and a board that compared asking prices would tell this
        // seller they were the cheapest on the shelf while a buyer saw them fourth.
        var row = Build(Mine(price: 180m, shipping: 30m),
        [
            Rival(190m, id: "A"), Rival(190m, id: "B"), Rival(190m, id: "C"),
        ]);

        Assert.Equal(PricePositionAnalyzer.Delivered, row.Basis);
        Assert.Equal(210m, row.MyComparedPrice);
        Assert.Equal(4, row.Rank);
        Assert.Equal("priced_out", row.Verdict);
    }

    [Fact]
    public void An_unreported_shipping_charge_is_not_free_shipping()
    {
        // eBay omits the shipping block entirely on some listings. Treating that as $0 would make
        // the seller look cheaper than they are on exactly the rows the board is about to
        // recommend a cut on, so the row drops to comparing asking prices and says which basis it
        // used rather than quietly inventing a delivered price.
        var row = Build(Mine(price: 180m, shippingKnown: false),
        [
            Rival(190m, shipping: 25m, id: "A"), Rival(195m, shipping: 25m, id: "B"), Rival(200m, id: "C"),
        ]);

        Assert.Equal(PricePositionAnalyzer.ItemPrice, row.Basis);
        Assert.False(row.MyShippingKnown);
        Assert.Equal(180m, row.MyComparedPrice);
        // Compared on asking price, the seller leads — and the row says that is the basis it used.
        Assert.Equal("leading", row.Verdict);
    }

    [Fact]
    public void On_the_delivered_basis_a_rival_who_states_no_shipping_is_shown_but_never_ranked()
    {
        // Freight and local-pickup listings state no shipping cost. Counted as free shipping, a
        // $900 pallet lands at the front of the shelf and every real listing behind it reads as
        // grossly overpriced.
        var row = Build(Mine(price: 300m),
        [
            Rival(120m, shippingStated: false, id: "FREIGHT"),
            Rival(310m, id: "A"), Rival(320m, id: "B"), Rival(330m, id: "C"),
        ]);

        var freight = row.Rivals.Single(r => r.ItemId == "FREIGHT");
        Assert.False(freight.Counted);
        Assert.Contains("shipping", freight.SkipReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, row.RivalsCounted);
        Assert.Equal("leading", row.Verdict);
    }

    // ── The refusals ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_repair_service_is_not_competition_however_cheap_it_is()
    {
        // The exact failure NonItemListingDetector exists for, on the sell side this time: a $1
        // "repair evaluation" carrying the real model number sorts to the top of a cheapest-first
        // search, and counted as a rival it reports a 20,000% premium on a fairly-priced listing.
        var row = Build(Mine(price: 300m),
        [
            Rival(1m, "Bitmain Antminer S19 95TH Repair Evaluation Service", id: "JUNK"),
            Rival(310m, id: "A"), Rival(320m, id: "B"), Rival(330m, id: "C"),
        ]);

        Assert.False(row.Rivals.Single(r => r.ItemId == "JUNK").Counted);
        Assert.Equal("leading", row.Verdict);
        Assert.Null(row.Cautions.FirstOrDefault(c => c.Contains("20,000")));
    }

    [Fact]
    public void An_auction_mid_bid_is_not_an_asking_price()
    {
        // An auction sitting at $9 on day one of seven is not a $9 competitor. Left in, half the
        // shelf on any auction-heavy category is made of prices nobody can actually buy at.
        var row = Build(Mine(price: 300m),
        [
            Rival(9m, id: "AUCTION", buyingOption: "AUCTION"),
            Rival(310m, id: "A"), Rival(320m, id: "B"), Rival(330m, id: "C"),
        ]);

        var auction = row.Rivals.Single(r => r.ItemId == "AUCTION");
        Assert.False(auction.Counted);
        Assert.Contains("bid", auction.SkipReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("leading", row.Verdict);
    }

    [Fact]
    public void A_lot_of_ten_is_not_a_rival_to_one()
    {
        var row = Build(Mine(price: 300m),
        [
            Rival(2400m, "Lot of 10 Bitmain Antminer S19 95TH Bitcoin Miner", id: "LOT"),
            Rival(310m, id: "A"), Rival(320m, id: "B"), Rival(330m, id: "C"),
        ]);

        var lot = row.Rivals.Single(r => r.ItemId == "LOT");
        Assert.False(lot.Counted);
        Assert.Equal(3, row.RivalsCounted);
    }

    [Fact]
    public void A_for_parts_unit_is_not_competition_for_a_working_one()
    {
        // Condition is not a tie-break, it is a different product to a buyer. Priced against a $40
        // for-parts unit, a working one is told to sell at scrap.
        var row = Build(Mine(price: 300m, condition: "Used"),
        [
            Rival(40m, id: "PARTS", condition: "For parts or not working"),
            Rival(310m, id: "A"), Rival(320m, id: "B"), Rival(330m, id: "C"),
        ]);

        Assert.False(row.Rivals.Single(r => r.ItemId == "PARTS").Counted);
        Assert.Equal("leading", row.Verdict);
    }

    [Fact]
    public void An_unknown_condition_never_rejects_a_rival()
    {
        // eBay's condition strings vary by category and plenty of listings carry none at all. A
        // bucket test that treated "unknown" as a mismatch would empty whole shelves and report
        // "nobody else is selling this" about a product with forty listings.
        var row = Build(Mine(price: 300m, condition: ""),
        [
            Rival(250m, id: "A", condition: ""), Rival(260m, id: "B", condition: "Used"),
            Rival(270m, id: "C", condition: "Brand New"),
        ]);

        Assert.Equal(3, row.RivalsCounted);
    }

    [Fact]
    public void The_cheapest_listing_on_a_shelf_is_stepped_over_when_it_is_far_under_the_rest()
    {
        // The cheapest thing on a shelf is routinely broken, mislabelled, or a seller with no
        // feedback who will never ship it. Chasing that price is how a good margin dies in an
        // afternoon, so the target is the cheapest listing a seller could plausibly be losing the
        // sale to — and the row says out loud that it stepped over one.
        var row = Build(Mine(price: 300m),
        [
            Rival(60m, id: "OUTLIER"), Rival(280m, id: "A"), Rival(290m, id: "B"), Rival(300m, id: "C"),
        ]);

        Assert.Equal(60m, row.CheapestRival);
        Assert.Equal(280m, row.TargetRival);
        Assert.True(row.TargetSkippedAnOutlier);
        Assert.Contains(row.Cautions, c => c.Contains("$60.00"));
    }

    [Fact]
    public void Two_rivals_is_not_a_shelf()
    {
        // "You are 2nd of 2" is a sentence with no information in it and a markdown attached.
        var row = Build(Mine(price: 300m), [Rival(200m, id: "A"), Rival(210m, id: "B")]);

        Assert.Equal("thin_market", row.Verdict);
        Assert.Null(row.Rank);
        Assert.Null(row.ItemPriceToLead);
        Assert.Equal("none", row.Blocker);
    }

    [Fact]
    public void Being_the_only_one_selling_it_is_pricing_power_not_a_problem()
    {
        var row = Build(Mine(price: 300m), []);

        Assert.Equal("alone", row.Verdict);
        Assert.Equal("none", row.Blocker);
        Assert.Contains("pricing power", row.Headline);
    }

    // ── The money ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_price_the_seller_cannot_afford_to_match_is_a_buying_problem_not_a_pricing_one()
    {
        // The single most expensive mistake this board could make. The shelf starts at $120 and the
        // seller paid $180 — "cut to $119.99" turns a slow listing into a fast loss, and the whole
        // point of borrowing NetProceedsCalculator's floor is that this verdict exists at all.
        var row = Build(Mine(price: 300m),
            [Rival(120m, id: "A"), Rival(125m, id: "B"), Rival(130m, id: "C")],
            cost: Cost(180m));

        Assert.Equal("cant_win", row.Verdict);
        Assert.Equal("supply", row.Blocker);
        Assert.Contains("buying problem", row.Headline);
    }

    [Fact]
    public void The_floor_it_refuses_to_cross_is_the_same_one_the_offers_screen_negotiates_against()
    {
        // Two screens deciding independently what "below cost" means is two screens that drift, and
        // the drift shows up as this board recommending a price the offers board would refuse.
        var mine = Mine(price: 300m);
        var row = Build(mine, [Rival(120m, id: "A"), Rival(125m, id: "B"), Rival(130m, id: "C")], cost: Cost(180m));

        var quote = new NetProceedsCalculator(new ProfitCalculator())
            .Quote(askPrice: mine.Price, unitCost: 180m, fees: Fees, buyerPaidShipping: 0m);

        Assert.Equal(quote.MinimumOfferPrice, row.FloorPrice);
        Assert.True(row.ItemPriceToLead < row.FloorPrice, "leading this shelf costs more than the seller can afford");
    }

    [Fact]
    public void A_cut_it_can_afford_is_offered_with_what_is_left_after_it()
    {
        // The number beside the recommended price is the take-home AFTER the cut, never the size of
        // the cut. A board that led with "save the sale" and hid "for $6" is selling somebody their
        // own markdown.
        var row = Build(Mine(price: 400m),
            [Rival(300m, id: "A"), Rival(310m, id: "B"), Rival(320m, id: "C")],
            cost: Cost(120m));

        Assert.Equal("priced_out", row.Verdict);
        Assert.Equal("price", row.Blocker);
        Assert.Equal(299.99m, row.PriceToLead);
        Assert.Equal(299.99m, row.ItemPriceToLead);
        Assert.NotNull(row.NetProfitAtLeadPrice);
        Assert.True(row.NetProfitAtLeadPrice > 0m);
    }

    [Fact]
    public void The_price_to_type_has_the_sellers_own_shipping_taken_out_of_it()
    {
        // The board compares delivered prices; the seller types an ASKING price. Handing them the
        // delivered figure to type would put them $25 over the front of the shelf they were just
        // told to lead.
        var row = Build(Mine(price: 400m, shipping: 25m),
            [Rival(300m, id: "A"), Rival(310m, id: "B"), Rival(320m, id: "C")],
            cost: Cost(120m));

        Assert.Equal(299.99m, row.PriceToLead);
        Assert.Equal(274.99m, row.ItemPriceToLead);
    }

    [Fact]
    public void A_shipping_charge_bigger_than_the_whole_shelf_gets_no_price_to_type()
    {
        // Rather than a negative asking price, which is what the arithmetic gives.
        var row = Build(Mine(price: 60m, shipping: 90m),
            [Rival(80m, id: "A"), Rival(85m, id: "B"), Rival(90m, id: "C")],
            cost: Cost(20m));

        Assert.Null(row.ItemPriceToLead);
        Assert.Contains(row.Cautions, c => c.Contains("shipping", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void With_no_cost_recorded_the_board_still_places_the_listing_but_says_what_it_cannot_check()
    {
        // Most listings have no cost basis. Withholding the position too would empty the board;
        // pretending the floor exists would recommend a loss. It reports the position and names
        // the one thing it does not know.
        var row = Build(Mine(price: 400m), [Rival(300m, id: "A"), Rival(310m, id: "B"), Rival(320m, id: "C")]);

        Assert.Equal("priced_out", row.Verdict);
        Assert.Null(row.FloorPrice);
        Assert.Null(row.NetProfitAtLeadPrice);
        Assert.Contains(row.Cautions, c => c.Contains("No cost is recorded"));
    }

    // ── The second axis ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_listing_nobody_has_seen_is_never_told_to_cut_its_price()
    {
        // The insight the whole screen exists for. Cheapest on the shelf and four views in sixty
        // days: buyers are not rejecting this price, they are not arriving at it. Telling this
        // seller to cut is telling them to give away margin to fix a title.
        var row = Build(Mine(price: 200m, views: 4, daysListed: 60),
            [Rival(300m, id: "A"), Rival(310m, id: "B"), Rival(320m, id: "C")]);

        Assert.Equal("leading", row.Verdict);
        Assert.Equal("visibility", row.Blocker);
        Assert.Contains(row.Cautions, c => c.Contains("4 views in 60 days"));
    }

    [Fact]
    public void A_zero_from_an_api_that_reports_nothing_is_not_a_zero()
    {
        // eBay returns HitCount on some accounts and omits it on others. With the scan reporting no
        // views anywhere, every listing on the board would read as invisible and every real pricing
        // verdict would be buried under a caution about a number that was never measured.
        var row = Build(Mine(price: 200m, views: 0, daysListed: 60),
            [Rival(300m, id: "A"), Rival(310m, id: "B"), Rival(320m, id: "C")],
            viewsReported: false);

        Assert.False(row.ViewsKnown);
        Assert.Equal("none", row.Blocker);
        Assert.DoesNotContain(row.Cautions, c => c.Contains("view"));
    }

    [Fact]
    public void A_listing_up_for_three_days_has_not_had_a_fair_run_in_front_of_anybody()
    {
        var row = Build(Mine(price: 200m, views: 1, daysListed: 3),
            [Rival(300m, id: "A"), Rival(310m, id: "B"), Rival(320m, id: "C")]);

        Assert.Equal("none", row.Blocker);
    }

    [Fact]
    public void Being_dear_outranks_being_invisible_because_only_one_of_them_has_an_action()
    {
        // A listing that is both 40% over the shelf and short of views gets the price verdict: the
        // cut is the step the seller can take today, and on eBay a high price is one of the reasons
        // a listing is not being surfaced in the first place.
        var row = Build(Mine(price: 500m, views: 2, daysListed: 90),
            [Rival(300m, id: "A"), Rival(310m, id: "B"), Rival(320m, id: "C")],
            cost: Cost(100m));

        Assert.Equal("price", row.Blocker);
    }

    [Fact]
    public void Watchers_at_your_price_are_offered_an_offer_rather_than_a_public_markdown()
    {
        // They found the listing and stopped at the number. An offer reaches exactly them and
        // costs nothing if nobody takes it; a markdown gives the same money to everybody.
        var row = Build(Mine(price: 400m, watchers: 6),
            [Rival(300m, id: "A"), Rival(310m, id: "B"), Rival(320m, id: "C")],
            cost: Cost(120m));

        Assert.Contains(row.Cautions, c => c.Contains("watching"));
    }

    // ── The board ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_board_leads_with_dollars_behind_the_shelf_not_with_percent_over_it()
    {
        // A 60% premium on a $14 item is $8. A 12% premium on a $1,900 miner is $228. A seller with
        // twenty minutes should be looking at the second one, and a percentage ranking sends them
        // to the first.
        var small = Build(Mine(price: 24m, title: "Anker USB C Cable 6ft"),
            [Rival(15m, "Anker USB C Cable 6ft", id: "s1"), Rival(15m, "Anker USB C Cable 6ft", id: "s2"),
             Rival(16m, "Anker USB C Cable 6ft", id: "s3")]);
        var big = Build(Mine(price: 1900m), [Rival(1700m, id: "b1"), Rival(1710m, id: "b2"), Rival(1720m, id: "b3")]);

        var ranked = PricePositionAnalyzer.Rank([small, big]);

        Assert.True(small.PremiumPercent > big.PremiumPercent, "the small item is the bigger percentage");
        Assert.Equal(big.Title, ranked[0].Title);
    }

    [Fact]
    public void A_row_with_no_answer_never_outranks_a_row_with_one()
    {
        var pricedOut = Build(Mine(price: 400m), [Rival(300m, id: "A"), Rival(310m, id: "B"), Rival(320m, id: "C")]);
        var alone = Build(Mine(price: 9000m), []);
        var failed = Analyzer().Failed(Mine(price: 8000m), "eBay search failed.", Now);

        var ranked = PricePositionAnalyzer.Rank([alone, failed, pricedOut]);

        Assert.Equal("priced_out", ranked[0].Verdict);
        Assert.Equal("lookup_failed", ranked[^1].Verdict);
    }

    [Fact]
    public void The_headline_figure_is_the_sellers_own_capital_not_the_size_of_the_cut()
    {
        // And the profit figure beside it is what is LEFT after moving to the front — with the
        // rows that could not be costed counted separately rather than silently dropped, because a
        // total that quietly excludes half the board is a total nobody can act on.
        var costed = Build(Mine(price: 400m), [Rival(300m, id: "A"), Rival(310m, id: "B"), Rival(320m, id: "C")], cost: Cost(120m));
        var uncosted = Build(Mine(price: 500m), [Rival(300m, id: "D"), Rival(310m, id: "E"), Rival(320m, id: "F")]);

        var summary = PricePositionAnalyzer.Summarize([costed, uncosted]);

        Assert.Equal(2, summary.PricedOut);
        Assert.Equal(900m, summary.CapitalBehindTheShelf);
        Assert.Equal(1, summary.PricedOutWithoutCost);
        Assert.Equal(costed.NetProfitAtLeadPrice, summary.ProfitStillOnTheTable);
    }

    [Fact]
    public void A_failed_search_is_one_row_without_an_answer_never_a_wrong_one()
    {
        var row = Analyzer().Failed(Mine(), "eBay listing search failed (HTTP 503).", Now);

        Assert.Equal("lookup_failed", row.Verdict);
        Assert.Equal("none", row.Blocker);
        Assert.Null(row.Rank);
        Assert.Null(row.ItemPriceToLead);
        Assert.Contains("503", row.Cautions[0]);
    }

    [Fact]
    public void The_shelf_is_searched_with_the_same_words_the_sourcing_boards_use()
    {
        // A product the seller is told to go and buy on one screen and told they are eighth on in
        // another must be the same search, or the two screens are describing different shelves.
        Assert.Equal(
            JackpotHunter.ShoppingQuery("Bitmain Antminer S19 95TH Bitcoin Miner", maxWords: 6),
            PricePositionAnalyzer.ShelfQuery("Bitmain Antminer S19 95TH Bitcoin Miner"));
    }

    [Fact]
    public void The_sellers_own_listing_is_never_counted_as_competition()
    {
        // eBay returns it like any other result, and counted it makes every listing exactly tie
        // with itself — one phantom rival on every row on the board.
        var row = Build(Mine(price: 300m),
        [
            Rival(300m, id: "MINE-1"), Rival(310m, id: "A"), Rival(320m, id: "B"), Rival(330m, id: "C"),
        ]);

        Assert.DoesNotContain(row.Rivals, r => r.ItemId == "MINE-1");
        Assert.Equal(3, row.RivalsCounted);
    }
}
