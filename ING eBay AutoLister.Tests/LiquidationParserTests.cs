using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The liquidation parser's job is refusal, and these mostly pin what it refuses.
//
// The stakes are higher here than on any other source, because a liquidation row can be eight of
// something: a wrong unit count does not shade a number, it multiplies it. So a count is used only
// where the listing stated one, a lot whose contents are "assorted" is not priced at all, and the
// site's own placeholder bid is refused by name.
//
// The thresholds these pin were measured against 801 live auction lots pulled across eight searches
// while the feature was built — the counts are recorded in LiquidationSelectors next to each rule.
public class LiquidationParserTests
{
    private static readonly LiquidationFeed Feed = LiquidationCatalog.Feeds[0];
    private static readonly DateTime Now = new(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);

    // ── The page ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A search page in the shape the site really ships: an escaped JSON island holding a flat
    /// store of "Type:id" entries with {"__ref": …} pointers between them.
    /// </summary>
    private static string Page(string lotJson, string auctionJson = "")
    {
        var auction = auctionJson.Length > 0 ? auctionJson : """
            "__typename":"Auction","id":750529,"eventName":"Overstock Product Liquidation",
            "eventCity":"Lindon","eventState":"UT","buyerPremiumRate":1.15,"buyerPremium":"15% Buyers Premium"
            """;

        var json =
            "{\"apollo.state\":{" +
            "\"Lot:1\":{" + lotJson + "}," +
            "\"Auction:750529\":{" + auction + "}," +
            "\"Auctioneer:9\":{\"__typename\":\"Auctioneer\",\"name\":\"Redwood Auctions\"}" +
            "}}";

        // The framework's own five-character escaping, which is what ExtractState has to undo.
        var escaped = json
            .Replace("&", "&a;").Replace("\"", "&q;").Replace("'", "&s;")
            .Replace("<", "&l;").Replace(">", "&g;");

        return $"<html><body><script id=\"hibid-state\" type=\"application/json\">{escaped}</script></body></html>";
    }

    private static string Lot(
        string lead, decimal highBid = 0m, decimal minBid = 5m, int quantity = 1,
        string description = "", string extra = "") =>
        $$"""
          "__typename":"Lot","id":1,"lead":"{{lead}}","quantity":{{quantity}},
          "description":"{{description}}","bidAmount":123.45,
          "auction":{"__ref":"Auction:750529"},
          "lotState":{"__typename":"LotState","highBid":{{highBid}},"minBid":{{minBid}},
                      "bidCount":2,"isClosed":false,"status":"OPEN","timeLeftSeconds":7200,
                      "timeLeft":"2h  0m  "}{{extra}}
          """;

    // Nullable, because one of these tests passes null on purpose: ParsePage takes a string?
    // and a scraped page that came back as nothing must yield an empty slice, not an exception.
    private static List<ING_eBay_AutoLister.Models.LocalSupplyListing> Parse(string? html) =>
        LiquidationParser.ParsePage(html, Feed, Now);

    // ── ExtractState ─────────────────────────────────────────────────────────

    [Fact]
    public void The_state_island_is_unescaped_with_the_frameworks_own_scheme_not_html_entities()
    {
        // A &quot; a seller typed into a lot description is legitimate JSON text. Running an HTML
        // decoder over the island turns it into a bare quote and produces an unterminated string —
        // which is how the whole page silently parses to zero lots.
        var listings = Parse(Page(Lot("Notes: &quot;call first&quot; Dyson V8", highBid: 40m)));

        var listing = Assert.Single(listings);
        Assert.Contains("&quot;", listing.Title);
    }

    [Fact]
    public void A_page_with_no_state_island_yields_nothing_rather_than_throwing()
    {
        Assert.Empty(Parse("<html><body>nothing here</body></html>"));
        Assert.Empty(Parse(""));
        Assert.Empty(Parse(null));
    }

    [Fact]
    public void Malformed_json_degrades_to_an_empty_slice_rather_than_a_failed_scan()
    {
        Assert.Empty(Parse("<script id=\"hibid-state\" type=\"application/json\">{not json</script>"));
    }

    // ── The placeholder bid ──────────────────────────────────────────────────

    [Fact]
    public void The_placeholder_bidAmount_is_never_read_as_a_price()
    {
        // Every one of 801 live lots carried bidAmount: 123.45. It is a client-side sentinel, and
        // reading it would have given every row on the board the same invented cost basis.
        var listing = Assert.Single(Parse(Page(Lot("Dyson V8 Cordless Vacuum", highBid: 0m, minBid: 5m))));

        Assert.Equal(5m, listing.Price);
        Assert.NotEqual(LiquidationSelectors.SentinelBidAmount, listing.Price);
    }

    [Fact]
    public void A_lot_with_no_bid_and_no_opening_bid_is_dropped_rather_than_priced_at_the_placeholder()
    {
        Assert.Empty(Parse(Page(Lot("Dyson V8 Cordless Vacuum", highBid: 0m, minBid: 0m))));
    }

    [Fact]
    public void The_live_bid_wins_over_the_opening_bid()
    {
        var listing = Assert.Single(Parse(Page(Lot("Dyson V8 Cordless Vacuum", highBid: 45m, minBid: 5m))));

        Assert.Equal(45m, listing.Price);
        Assert.False(listing.Liquidation!.IsStartingBid);
        Assert.Contains("current bid", listing.PriceText);
    }

    [Fact]
    public void With_no_bids_the_opening_bid_is_flagged_as_one()
    {
        // "Nobody has bid yet" is the difference between a floor and a contest, and it is the
        // cheapest this lot will ever be.
        var listing = Assert.Single(Parse(Page(Lot("Dyson V8 Cordless Vacuum", highBid: 0m, minBid: 5m))));

        Assert.True(listing.Liquidation!.IsStartingBid);
        Assert.Contains("opening bid", listing.PriceText);
    }

    // ── Lots you cannot act on ───────────────────────────────────────────────

    [Fact]
    public void A_closed_lot_is_dropped()
    {
        var closed = Lot("Dyson V8", highBid: 40m).Replace("\"isClosed\":false", "\"isClosed\":true");
        Assert.Empty(Parse(Page(closed)));
    }

    [Fact]
    public void A_lot_with_no_internet_bidding_is_dropped()
    {
        // Real and common: the catalogue is published but the sale is in the room only. A ranking
        // full of things you cannot buy from here is worse than a shorter one.
        var floorOnly = Lot("Dyson V8", highBid: 40m).Replace("\"status\":\"OPEN\"", "\"status\":\"POSTED_NO_INTERNET_BIDDING\"");
        Assert.Empty(Parse(Page(floorOnly)));
    }

    [Theory]
    [InlineData("Remington 870 Shotgun")]
    [InlineData("Case of Wine - 12 Bottles")]
    [InlineData("2014 Ford Pickup Truck")]
    [InlineData("Real Estate: 40 Acre Parcel")]
    public void Things_an_eBay_seller_cannot_list_are_dropped(string lead)
    {
        // Firearms, alcohol and tobacco are prohibited outright; a vehicle or a parcel of land is
        // not an eBay listing at all. Auction catalogues are full of all of them.
        Assert.Empty(Parse(Page(Lot(lead, highBid: 200m))));
    }

    // ── How many things is this ──────────────────────────────────────────────

    [Theory]
    [InlineData("(4) DeWalt Grinders", 4)]
    [InlineData("Lot of 8 Rorsou Corded Headphones", 8)]
    [InlineData("Lot Of 3 DeWalt Batteries", 3)]
    [InlineData("Case of 12 Bosch Drill Bits", 12)]
    [InlineData("Sony Earbuds 24 pcs", 24)]
    public void A_stated_unit_count_is_read(string lead, int expected)
    {
        var size = LiquidationParser.ReadUnits(lead, "", siteQuantity: 1);

        Assert.Equal(expected, size.Count);
        Assert.True(size.IsLot);
        Assert.Null(size.Reason);
    }

    [Theory]
    [InlineData("Uline 8000 lb. Pallet Jack")]
    [InlineData("Garvee 43\" Clamp-On Pallet Forks")]
    [InlineData("Wire Pallet Rack 2 Sections")]
    public void The_word_pallet_alone_never_implies_a_quantity(string lead)
    {
        // Measured live: of 37 lots whose titles contained "pallet", nearly all were pallet jacks,
        // forks and racks — single products named after the thing. Treating those as pallets of
        // stock would multiply a pallet jack's resale by an invented unit count.
        var size = LiquidationParser.ReadUnits(lead, "", siteQuantity: 1);

        Assert.Equal(1, size.Count);
        Assert.False(size.IsLot);
        Assert.Null(size.Reason);
    }

    [Fact]
    public void Bulk_stock_with_no_stated_count_is_refused_rather_than_guessed()
    {
        // A pallet's resale value is the per-unit price times a number nobody published. There is
        // no honest way to price it, and inventing a divisor is the one thing that must not happen.
        var size = LiquidationParser.ReadUnits("PALLET OF ASSORTED SMALL APPLIANCES", "", siteQuantity: 1);

        Assert.True(size.IsLot);
        Assert.NotNull(size.Reason);
        Assert.Contains("how many", size.Reason);
    }

    [Fact]
    public void A_count_stated_after_the_bulk_wording_is_read_rather_than_refused()
    {
        // Found by running the parser over 661 real lots: this exact title was being refused as
        // "no count stated" while stating its count perfectly clearly. Refusing costs coverage on a
        // lot the seller could have bought.
        var size = LiquidationParser.ReadUnits("LOT OF (2) DeWalt Rotary Hammer Drills", "", siteQuantity: 1);

        Assert.Equal(2, size.Count);
        Assert.True(size.IsLot);
        Assert.Null(size.Reason);
    }

    [Fact]
    public void A_bracketed_number_is_only_read_as_a_count_inside_bulk_wording()
    {
        // Outside it, "(2)" in the middle of a title could be anything — a voltage, a size, a
        // model. Only the "lot of" context makes it a quantity.
        var size = LiquidationParser.ReadUnits("DeWalt Conduit Reamer (2) Inch #DWA26001R", "", siteQuantity: 1);

        Assert.Equal(1, size.Count);
        Assert.False(size.IsLot);
    }

    [Fact]
    public void The_sites_own_quantity_field_is_believed_when_the_title_says_nothing()
    {
        var size = LiquidationParser.ReadUnits("Wicked Audio Bluetooth Headphones", "", siteQuantity: 5);

        Assert.Equal(5, size.Count);
        Assert.True(size.IsLot);
    }

    [Fact]
    public void An_implausible_count_is_refused_as_a_model_number_not_multiplied()
    {
        // "1200 count" on a lot of one is a spec. Multiplying a resale price by it would put a
        // fictional five-figure row at the very top of the ranking.
        var size = LiquidationParser.ReadUnits("Stretch Wrap 1200 ct Roll", "", siteQuantity: 1);

        Assert.Equal(1, size.Count);
        Assert.NotNull(size.Reason);
    }

    // ── Whether there is a product to price ──────────────────────────────────

    [Theory]
    [InlineData("(6) Assorted Power Tools")]
    [InlineData("Lot of 4 Various Kitchen Gadgets")]
    [InlineData("(10) Misc. Electronics")]
    public void An_assorted_lot_is_not_priced_against_a_single_comp(string lead)
    {
        var size = LiquidationParser.ReadUnits(lead, "", 1);
        var reason = LiquidationParser.UnpriceableReason(lead, lead, size);

        Assert.NotNull(reason);
        Assert.Contains("assorted", reason);
    }

    [Fact]
    public void A_lot_listing_several_different_items_has_no_single_comp_to_multiply()
    {
        // Straight off the live board: "(3) NASCAR Headphones, New Glue Gun, Small Tripod".
        const string lead = "(3) NASCAR Headphones, New Glue Gun, Small Tripod";
        var size = LiquidationParser.ReadUnits(lead, "", 1);

        Assert.NotNull(LiquidationParser.UnpriceableReason(lead, lead, size));
    }

    [Fact]
    public void A_comma_in_a_single_items_title_is_just_punctuation()
    {
        // The list rule applies only to lots. On one item a comma is ordinary, and refusing on it
        // would empty the board for no gain.
        const string lead = "Sony WH-1000XM4 Wireless Headphones, Black";
        var size = LiquidationParser.ReadUnits(lead, "", 1);

        Assert.False(size.IsLot);
        Assert.Null(LiquidationParser.UnpriceableReason(lead, lead, size));
    }

    [Fact]
    public void For_parts_stock_is_refused_because_the_comps_behind_it_are_for_items_that_work()
    {
        var size = LiquidationParser.ReadUnits("Dyson V8 For Parts Only", "", 1);
        var reason = LiquidationParser.UnpriceableReason("Dyson V8 For Parts Only", "Dyson V8 For Parts Only", size);

        Assert.NotNull(reason);
        Assert.Contains("parts", reason);
    }

    [Fact]
    public void As_is_alone_is_not_a_refusal()
    {
        // Measured at 56 of 801 live lots: it is boilerplate half the auction houses staple to
        // every lot they list, and refusing on it would delete a large slice of a fine board.
        const string lead = "Dyson V8 Cordless Vacuum - AS IS";
        var size = LiquidationParser.ReadUnits(lead, "", 1);

        Assert.Null(LiquidationParser.UnpriceableReason(lead, lead, size));
    }

    // ── Grade ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Pallet of Uninspected Customer Returns", "uninspected_returns")]
    [InlineData("Lot of 20 Tested Customer Returns", "customer_returns")]
    [InlineData("Lot of 6 Open Box Blenders", "open_box")]
    [InlineData("Lot of 6 Factory Sealed Blenders", "new")]
    [InlineData("Lot of 6 Shelf Pull Blenders", "shelf_pull")]
    [InlineData("Lot of 6 Damaged Blenders", "salvage")]
    public void Condition_wording_maps_to_the_lot_analyzers_own_grades(string wording, string expected)
    {
        Assert.Equal(expected, LiquidationParser.GradeFor(wording));

        // And every grade this returns has to be one the manifest analyzer actually knows.
        Assert.Equal(expected, LotAnalyzer.GradeFor(expected).Id);
    }

    [Fact]
    public void A_bare_new_is_graded_as_a_shelf_pull_not_as_factory_sealed()
    {
        // "New" appeared in 122 of 801 live lots. At an auction it describes the packaging far more
        // often than it guarantees a factory seal, and overstating recovery costs a bought lot.
        Assert.Equal("shelf_pull", LiquidationParser.GradeFor("NEW Lot of 2 Chainsaw Chains"));
    }

    [Fact]
    public void An_ungraded_lot_gets_the_mixed_assumptions_not_the_best_case()
    {
        Assert.Equal(LiquidationSelectors.DefaultLotGradeId, LiquidationParser.GradeFor("Lot of 4 Blenders"));
        Assert.Equal("mixed", LiquidationSelectors.DefaultLotGradeId);
    }

    // ── The buyer's premium ──────────────────────────────────────────────────

    [Fact]
    public void A_published_premium_rate_is_read_as_a_percentage()
    {
        var listing = Assert.Single(Parse(Page(Lot("Dyson V8", highBid: 40m))));

        Assert.Equal(15m, listing.Liquidation!.BuyerPremiumPercent);
        Assert.False(listing.Liquidation.BuyerPremiumAssumed);
    }

    [Fact]
    public void An_unpublished_premium_is_read_off_the_printed_terms()
    {
        var auction = """
            "__typename":"Auction","id":750529,"eventName":"Estate Sale","eventCity":"Reno","eventState":"NV",
            "buyerPremiumRate":1,"buyerPremium":"18.00 % Auctioneer's fees"
            """;
        var listing = Assert.Single(Parse(Page(Lot("Dyson V8", highBid: 40m), auction)));

        Assert.Equal(18m, listing.Liquidation!.BuyerPremiumPercent);
        Assert.False(listing.Liquidation.BuyerPremiumAssumed);
    }

    [Fact]
    public void A_premium_stated_nowhere_is_assumed_rather_than_waived_and_says_so()
    {
        // A published zero and an unpublished premium look identical in this data. Of the two
        // mistakes only one costs money: assuming none where 15% is charged buys a loser.
        var auction = """
            "__typename":"Auction","id":750529,"eventName":"Estate Sale","eventCity":"Reno","eventState":"NV",
            "buyerPremiumRate":1,"buyerPremium":""
            """;
        var listing = Assert.Single(Parse(Page(Lot("Dyson V8", highBid: 40m), auction)));

        Assert.Equal(LiquidationLotPricer.AssumedBuyerPremiumPercent, listing.Liquidation!.BuyerPremiumPercent);
        Assert.True(listing.Liquidation.BuyerPremiumAssumed);
    }

    [Fact]
    public void An_absurd_premium_rate_is_ignored_rather_than_charged()
    {
        var auction = """
            "__typename":"Auction","id":750529,"eventName":"Estate Sale","eventCity":"Reno","eventState":"NV",
            "buyerPremiumRate":15,"buyerPremium":""
            """;
        var listing = Assert.Single(Parse(Page(Lot("Dyson V8", highBid: 40m), auction)));

        // 15 is not a 1400% premium; it is a different field or a typo, and charging it would blank
        // the whole board.
        Assert.Equal(LiquidationLotPricer.AssumedBuyerPremiumPercent, listing.Liquidation!.BuyerPremiumPercent);
    }

    // ── The event ────────────────────────────────────────────────────────────

    [Fact]
    public void A_going_out_of_business_event_is_flagged_and_an_ordinary_one_is_not()
    {
        var liquidation = Assert.Single(Parse(Page(Lot("Dyson V8", highBid: 40m))));
        Assert.True(liquidation.Liquidation!.IsLiquidationEvent);
        Assert.Equal("Overstock Product Liquidation", liquidation.Liquidation.EventName);

        var ordinary = """
            "__typename":"Auction","id":750529,"eventName":"Monthly Consignment Auction","eventCity":"Reno",
            "eventState":"NV","buyerPremiumRate":1.15,"buyerPremium":"15%"
            """;
        var plain = Assert.Single(Parse(Page(Lot("Dyson V8", highBid: 40m), ordinary)));
        Assert.False(plain.Liquidation!.IsLiquidationEvent);
    }

    [Fact]
    public void The_pickup_city_and_the_auction_house_are_carried_onto_the_row()
    {
        var listing = Assert.Single(Parse(Page(Lot("Dyson V8", highBid: 40m))));

        Assert.Equal("Lindon, UT", listing.Location);
        Assert.Equal(LiquidationCatalog.Site, listing.SourceLabel);
    }

    [Fact]
    public void The_closing_time_is_computed_from_the_countdown_not_from_a_zoneless_timestamp()
    {
        // The site prints its close time in the auction house's own zone with no offset on it.
        // Seconds remaining are exact and have no timezone to get wrong.
        var listing = Assert.Single(Parse(Page(Lot("Dyson V8", highBid: 40m))));

        Assert.Equal(Now.AddHours(2), listing.Liquidation!.ClosesUtc);
    }

    // ── Title tidying ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("(4) DeWalt Grinders", "DeWalt Grinders")]
    [InlineData("Lot 79 | Suohuang Laptop Cooling Fan", "Suohuang Laptop Cooling Fan")]
    [InlineData("Dyson V8 Cordless Vacuum - AS IS", "Dyson V8 Cordless Vacuum")]
    [InlineData("Bosch Drill Kit, Used", "Bosch Drill Kit")]
    public void The_auctions_own_conventions_come_off_before_the_comp_lookup(string raw, string expected)
    {
        // The count and the disclaimer reach ProductNormalizer as part of the product identity
        // otherwise, and a comp lookup for "(4) DeWalt Grinders" matches nothing.
        Assert.Equal(expected, LiquidationParser.CleanTitle(raw));
    }

    // ── The claimed retail value ─────────────────────────────────────────────

    [Theory]
    [InlineData("Pallet of Tools - $4,200 retail value", 4200)]
    [InlineData("Blender Lot, MSRP $199", 199)]
    [InlineData("Retail: $1,899 of Small Appliances", 1899)]
    public void The_listings_own_retail_claim_is_read_for_the_cross_check(string text, decimal expected)
    {
        Assert.Equal(expected, LiquidationParser.ReadClaimedRetail(text));
    }

    [Fact]
    public void No_retail_claim_reads_as_null_rather_than_zero()
    {
        Assert.Null(LiquidationParser.ReadClaimedRetail("Dyson V8 Cordless Vacuum"));
    }

    // ── Blocks and dedupe ────────────────────────────────────────────────────

    [Fact]
    public void A_challenge_page_served_with_a_200_is_detected_rather_than_read_as_an_empty_market()
    {
        Assert.NotNull(LiquidationParser.DetectBlock("<html><title>Just a moment...</title>"));
        Assert.NotNull(LiquidationParser.DetectBlock(""));
        Assert.Null(LiquidationParser.DetectBlock("<html><body>a real page of lots</body></html>"));
    }

    [Fact]
    public void The_word_access_denied_deep_in_a_real_page_is_not_a_block()
    {
        // Somebody's listing for a door lock genuinely does say "access denied".
        var page = new string('x', LiquidationSelectors.BlockScanChars + 100) + "access denied";
        Assert.Null(LiquidationParser.DetectBlock(page));
    }

    [Fact]
    public void The_same_lot_from_two_search_slices_is_one_row_not_two()
    {
        // The plain search and the "lot" slice legitimately both return it. It is one thing to buy.
        var page = Page(Lot("Lot of 8 Rorsou Corded Headphones", highBid: 30m));
        var result = LiquidationParser.BuildResult(
            [Parse(page), LiquidationParser.ParsePage(page, LiquidationCatalog.Feeds[1], Now)],
            "headphones", "89101", 40);

        Assert.Equal(1, result.Count);
    }

    [Fact]
    public void The_result_reports_the_radius_it_actually_searched_not_the_one_on_the_form()
    {
        // A scan that quietly searched 250 miles must never be reported as the 40 the form said.
        var result = LiquidationParser.BuildResult([Parse(Page(Lot("Dyson V8", highBid: 40m)))], "dyson", "89101", 40);

        Assert.Equal(LiquidationCatalog.MinRadiusMiles, result.RadiusMiles);
        Assert.Contains("widened from 40", result.ScopeLabel);
    }
}
