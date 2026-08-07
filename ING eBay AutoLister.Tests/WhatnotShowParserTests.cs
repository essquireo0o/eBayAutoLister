using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The lot on screen, read out of the show's own page.
/// </summary>
/// <remarks>
/// <para>
/// This parser reads somebody else's document, whose shape is theirs to change without telling
/// anyone, and hands what it finds to the box every number on the live card is derived from. So the
/// tests that matter here are not the happy ones — they are the refusals. A parser that guesses is
/// worse than no parser at all: a wrong <b>title</b> prices a different product and a wrong
/// <b>price</b> prices this one wrongly, and both arrive as a confident ceiling with a hammer coming
/// down on it.
/// </para>
/// <para>
/// Two of these are the whole file: a number is only divided by 100 where the key <i>says</i> cents,
/// and a bare <c>name</c> is only a title on an object that is otherwise a lot for sale.
/// </para>
/// </remarks>
public class WhatnotShowParserTests
{
    private const string ShowUrl = "https://www.whatnot.com/live/9f2c-abcd";

    // ── The address ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("whatnot.com", true)]
    [InlineData("www.whatnot.com", true)]
    [InlineData("m.whatnot.com", true)]
    [InlineData("WHATNOT.COM", true)]
    [InlineData("notwhatnot.com", false)]
    [InlineData("whatnot.com.example.com", false)]
    [InlineData("ebay.com", false)]
    [InlineData("", false)]
    public void Only_whatnot_is_whatnot(string host, bool expected) =>
        Assert.Equal(expected, WhatnotShowParser.IsWhatnotHost(host));

    [Fact]
    public void A_show_address_is_accepted()
    {
        var (ok, _, _, _) = WhatnotShowParser.ValidateShowUrl(ShowUrl);
        Assert.True(ok);
    }

    /// <summary>
    /// The read only knows one site's page shape. Fetching anything else would either fail to parse
    /// or — much worse — parse some other page's stray title into the box that decides the bid.
    /// </summary>
    [Fact]
    public void Another_site_is_refused_by_name()
    {
        var (ok, headline, _, hint) = WhatnotShowParser.ValidateShowUrl("https://www.ebay.com/itm/123");

        Assert.False(ok);
        Assert.Contains("ebay.com", headline, StringComparison.OrdinalIgnoreCase);
        // And it says what still works, because the typed box works on any platform.
        Assert.Contains("type the item", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://www.whatnot.com/live")]
    [InlineData("https://www.whatnot.com/live/")]
    [InlineData("https://www.whatnot.com")]
    public void The_browse_page_is_not_a_show(string url)
    {
        var (ok, headline, _, _) = WhatnotShowParser.ValidateShowUrl(url);

        // It lists a hundred shows and has no current lot. "Nothing on screen" about it would be
        // true and would read as a broken feature.
        Assert.False(ok);
        Assert.Contains("browse page", headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Something_that_is_not_an_address_is_refused_rather_than_fetched()
    {
        var (ok, _, _, _) = WhatnotShowParser.ValidateShowUrl("not a url at all");
        Assert.False(ok);
    }

    // ── Getting the data out of the page ─────────────────────────────────────────────────────

    [Fact]
    public void A_json_script_payload_is_found()
    {
        var html = """
            <html><body><script id="__NEXT_DATA__" type="application/json">{"a":{"b":1}}</script></body></html>
            """;

        Assert.Contains(WhatnotShowParser.JsonBlobs(html), b => b.Contains("\"b\":1", StringComparison.Ordinal));
    }

    [Fact]
    public void A_streamed_flight_chunk_is_decoded_and_found()
    {
        // The app-router shape: the payload arrives as an escaped string pushed into an array, with
        // row markers between the objects — so the objects are cut out rather than the whole stream
        // being handed to a parser that would reject all of it.
        var html = """
            <script>self.__next_f.push([1,"3:{\"listing\":{\"title\":\"Nintendo Switch OLED\"}}\n"])</script>
            """;

        Assert.Contains(WhatnotShowParser.JsonBlobs(html),
            b => b.Contains("Nintendo Switch OLED", StringComparison.Ordinal));
    }

    [Fact]
    public void Half_decoded_text_is_dropped_rather_than_parsed()
    {
        // Half of an escaped string parses into confident nonsense, which is the one failure mode
        // this read cannot have.
        Assert.Equal("", WhatnotShowParser.UnescapeJsString("broken \\q escape"));
        Assert.Equal("", WhatnotShowParser.UnescapeJsString(null));
    }

    [Fact]
    public void An_unterminated_object_does_not_hang_or_throw()
    {
        var spans = WhatnotShowParser.ObjectSpans("""{"title":"never closed" """);
        Assert.Empty(spans);
    }

    [Fact]
    public void Braces_inside_strings_do_not_end_an_object()
    {
        var spans = WhatnotShowParser.ObjectSpans("""{"title":"a } inside a string","id":"7"}""");

        Assert.Single(spans);
        Assert.EndsWith("""{"title":"a } inside a string","id":"7"}""", spans[0], StringComparison.Ordinal);
    }

    // ── Reading the lot ──────────────────────────────────────────────────────────────────────

    private const string PagesRouterShow = """
        <html><body><script id="__NEXT_DATA__" type="application/json">
        {"props":{"pageProps":{"liveStream":{"id":"ls_1","title":"Miner Monday - everything must go",
        "activeListing":{"__typename":"Listing","id":"lst_9931","title":"Bitmain Antminer S19j Pro 104TH",
        "currentPrice":245.50,"imageUrl":"https://media.whatnot.com/lst_9931.jpg",
        "category":{"name":"Electronics"},"status":"ACTIVE"}}}}}
        </script></body></html>
        """;

    [Fact]
    public void The_lot_on_screen_is_read_off_the_page()
    {
        var read = WhatnotShowParser.Read(ShowUrl, PagesRouterShow);

        Assert.Equal(WhatnotReadStatuses.Read, read.Status);
        Assert.Equal("Bitmain Antminer S19j Pro 104TH", read.Title);
        Assert.Equal(245.50m, read.CurrentBid);
        Assert.Equal("currentPrice", read.BidKey);
        Assert.Equal("lst_9931", read.ListingId);
        Assert.Equal("https://media.whatnot.com/lst_9931.jpg", read.ImageUrl);
        Assert.Equal("Electronics", read.CategoryHint);
    }

    /// <summary>
    /// The show's own title is a title on the page too, and it is not the thing being sold. The lot
    /// wins because the page said it was the active one, which is worth more than every other signal
    /// on the object combined.
    /// </summary>
    [Fact]
    public void The_shows_name_is_not_mistaken_for_the_lot()
    {
        var read = WhatnotShowParser.Read(ShowUrl, PagesRouterShow);

        Assert.DoesNotContain("Miner Monday", read.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_app_router_shape_reads_the_same_way()
    {
        var html = """
            <script>self.__next_f.push([1,"3:{\"currentListing\":{\"__typename\":\"LiveStreamListing\",\"id\":\"77\",\"name\":\"Goldshell Mini Doge II\",\"currentBid\":{\"amount\":\"$38.00\",\"currency\":\"USD\"}}}\n"])</script>
            """;

        var read = WhatnotShowParser.Read(ShowUrl, html);

        Assert.Equal(WhatnotReadStatuses.Read, read.Status);
        Assert.Equal("Goldshell Mini Doge II", read.Title);
        Assert.Equal(38.00m, read.CurrentBid);
    }

    /// <summary>
    /// <c>name</c> is on users, categories, shipping profiles and half of a framework's own objects.
    /// Reading it as a title anywhere it appears is how a read prices the seller's username.
    /// </summary>
    [Fact]
    public void A_bare_name_on_something_that_is_not_a_lot_is_not_a_title()
    {
        var html = """
            <script type="application/json">{"props":{"user":{"id":"u1","name":"minerdepot","followers":420},
            "category":{"id":"c9","name":"Trading Cards"}}}</script>
            """;

        var read = WhatnotShowParser.Read(ShowUrl, html);

        Assert.Equal(WhatnotReadStatuses.NoListing, read.Status);
        Assert.Equal("", read.Title);
    }

    [Fact]
    public void A_name_counts_where_the_object_is_otherwise_a_lot_for_sale()
    {
        var html = """
            <script type="application/json">{"activeListing":{"name":"Apple AirPods Pro 2","price":45}}</script>
            """;

        var read = WhatnotShowParser.Read(ShowUrl, html);

        Assert.Equal("Apple AirPods Pro 2", read.Title);
    }

    // ── Money ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("""{"activeListing":{"title":"A thing","price":1299}}""", 1299)]
    [InlineData("""{"activeListing":{"title":"A thing","price":"$1,299.00"}}""", 1299)]
    [InlineData("""{"activeListing":{"title":"A thing","currentBid":{"amount":42.5}}}""", 42.5)]
    [InlineData("""{"activeListing":{"title":"A thing","priceCents":8999}}""", 89.99)]
    [InlineData("""{"activeListing":{"title":"A thing","currentPrice":{"amountCents":2500}}}""", 25)]
    public void A_price_is_read_in_whatever_shape_the_page_wrote_it(string json, double expected)
    {
        var read = WhatnotShowParser.Read(ShowUrl, $"""<script type="application/json">{json}</script>""");

        Assert.Equal((decimal)expected, read.CurrentBid);
    }

    /// <summary>
    /// The one rule that keeps this honest: a number is cents only where the KEY says cents.
    /// Guessing from the size of the number is how a $1,299 lot becomes a $12.99 one on the screen
    /// that decides what to bid — and the mistake is invisible, because $12.99 is a plausible price.
    /// </summary>
    [Fact]
    public void A_large_price_is_not_quietly_assumed_to_be_cents()
    {
        var read = WhatnotShowParser.Read(ShowUrl,
            """<script type="application/json">{"activeListing":{"title":"Antminer S19","price":129900}}</script>""");

        Assert.Equal(129900m, read.CurrentBid);
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    public void A_price_that_is_not_a_price_is_left_off_rather_than_shown(int value)
    {
        var json = """{"activeListing":{"title":"A real thing","price":PRICE}}"""
            .Replace("PRICE", value.ToString());
        var read = WhatnotShowParser.Read(ShowUrl, $"""<script type="application/json">{json}</script>""");

        Assert.Null(read.CurrentBid);
        // And the absence is said, because a ceiling with no bid beside it still answers — it just
        // can't say how much room is left in it.
        Assert.Contains(read.Warnings, w => w.Contains("No price on the page", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// An opening price is where the bidding starts and not what anyone has bid. It goes in the box —
    /// it is the right starting point — and the read refuses to let it pass as a live number.
    /// </summary>
    [Fact]
    public void An_opening_price_is_put_in_the_box_and_labelled()
    {
        var read = WhatnotShowParser.Read(ShowUrl,
            """<script type="application/json">{"activeListing":{"title":"Vintage Zippo lot","startingPrice":5}}</script>""");

        Assert.Equal(5m, read.CurrentBid);
        Assert.True(read.BidIsOpeningPrice);
        Assert.Contains(read.Warnings, w => w.Contains("STARTS", StringComparison.Ordinal));
    }

    [Fact]
    public void A_live_bid_outranks_an_opening_price_on_the_same_lot()
    {
        var read = WhatnotShowParser.Read(ShowUrl,
            """<script type="application/json">{"activeListing":{"title":"A thing","startingPrice":5,"currentBid":80}}</script>""");

        Assert.Equal(80m, read.CurrentBid);
        Assert.False(read.BidIsOpeningPrice);
    }

    // ── The refusals ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_page_with_no_listing_in_it_says_so_rather_than_inventing_one()
    {
        var read = WhatnotShowParser.Read(ShowUrl, "<html><body><div id=\"root\"></div></body></html>");

        Assert.Equal(WhatnotReadStatuses.NoListing, read.Status);
        // Between lots is the normal state of a live show, so the sentence has to say that rather
        // than reading as a failure.
        Assert.Contains("between lots", read.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("", read.Hint);
    }

    [Fact]
    public void An_empty_page_is_not_a_lot()
    {
        Assert.Equal(WhatnotReadStatuses.NoListing, WhatnotShowParser.Read(ShowUrl, "").Status);
        Assert.Equal(WhatnotReadStatuses.NoListing, WhatnotShowParser.Read(ShowUrl, null).Status);
    }

    /// <summary>
    /// A lot called "#12" is a lot the sold search would answer about at random, and a random answer
    /// on this screen is a bid. Same bar as the pasted lot list, shared rather than restated.
    /// </summary>
    [Fact]
    public void A_lot_with_no_name_to_search_on_is_refused_and_quoted_back()
    {
        var read = WhatnotShowParser.Read(ShowUrl,
            """<script type="application/json">{"activeListing":{"title":"#12","price":5}}</script>""");

        Assert.Equal(WhatnotReadStatuses.NoListing, read.Status);
        Assert.Equal("", read.Title);
        // Quoted back, so the seller can see what the page actually said rather than guessing.
        Assert.Contains("#12", read.Detail, StringComparison.Ordinal);
        Assert.Equal("#12", read.RawTitle);
    }

    /// <summary>The same cleaning a pasted lot line gets, so a lot read off the page and the same lot
    /// pasted into the list arrive at the comp lookup identically.</summary>
    [Fact]
    public void A_lot_number_and_an_asking_price_come_off_the_title_the_same_way_a_pasted_line_does()
    {
        var read = WhatnotShowParser.Read(ShowUrl,
            """<script type="application/json">{"activeListing":{"title":"Lot 3: Nintendo Switch OLED - starting at $120"}}</script>""");

        Assert.Equal("Nintendo Switch OLED", read.Title);
        Assert.Equal(120m, read.CurrentBid);
        Assert.True(read.BidIsOpeningPrice);
        // And what was changed is said, because a clean that dropped the wrong half of a name is
        // only findable next to the original.
        Assert.Contains(read.Warnings, w => w.Contains("Lot 3: Nintendo Switch OLED", StringComparison.Ordinal));
        Assert.Equal("Lot 3: Nintendo Switch OLED - starting at $120", read.RawTitle);
    }

    [Fact]
    public void A_giveaway_is_flagged_rather_than_priced_silently()
    {
        var read = WhatnotShowParser.Read(ShowUrl,
            """<script type="application/json">{"activeListing":{"title":"Giveaway entry - follow to win","price":1}}</script>""");

        Assert.Contains(read.Warnings, w => w.Contains("giveaway", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_lot_the_page_marks_sold_loses_to_a_live_one()
    {
        var html = """
            <script type="application/json">{"props":{
            "pinnedListing":{"title":"Antminer S9 that already went","status":"SOLD","price":90},
            "activeListing":{"title":"Antminer S19j Pro on the block now","status":"ACTIVE","price":200}}}</script>
            """;

        var read = WhatnotShowParser.Read(ShowUrl, html);

        Assert.Equal("Antminer S19j Pro on the block now", read.Title);
    }

    [Fact]
    public void A_sold_lot_that_is_all_there_was_is_read_and_labelled()
    {
        // A show between lots. It is still worth saying what just went — and worth saying that it
        // went, so nobody prices a ceiling against a lot that is already somebody else's.
        var read = WhatnotShowParser.Read(ShowUrl,
            """<script type="application/json">{"activeListing":{"title":"Antminer S9 that already went","status":"SOLD","price":90}}</script>""");

        Assert.Equal(WhatnotReadStatuses.Read, read.Status);
        Assert.Contains(read.Warnings, w => w.Contains("just went", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_insecure_image_is_not_rendered_in_the_apps_own_page()
    {
        var read = WhatnotShowParser.Read(ShowUrl,
            """<script type="application/json">{"activeListing":{"title":"A thing","imageUrl":"http://media.example.com/x.jpg"}}</script>""");

        Assert.Equal("", read.ImageUrl);
    }

    [Fact]
    public void A_title_longer_than_a_product_name_is_cut_rather_than_searched_on_whole()
    {
        var json = """{"activeListing":{"title":"TITLE"}}""".Replace("TITLE", new string('a', 400));
        var read = WhatnotShowParser.Read(ShowUrl, $"""<script type="application/json">{json}</script>""");

        Assert.True(read.Title.Length <= WhatnotShowParser.MaxTitleLength);
    }

    // ── What the read is willing to claim ────────────────────────────────────────────────────

    /// <summary>
    /// This is a claim about a document the seller cannot see. Every field says which key in that
    /// document it came from, so a read that went wrong is inspectable rather than mysterious.
    /// </summary>
    [Fact]
    public void Every_read_carries_where_it_got_the_title()
    {
        var read = WhatnotShowParser.Read(ShowUrl, PagesRouterShow);

        Assert.Contains(read.Evidence, e => e.Contains("activeListing.title", StringComparison.Ordinal));
        Assert.Contains(read.Evidence, e => e.Contains("activeListing.currentPrice", StringComparison.Ordinal));
        Assert.Contains(read.Evidence, e => e.Contains("lst_9931", StringComparison.Ordinal));
    }

    /// <summary>
    /// The honest limit of reading a page instead of a socket, said on every SUCCESSFUL read and not
    /// only on the awkward ones. A seller who learns it from a screen that repeats it will not be
    /// caught out by it at $200.
    /// </summary>
    [Fact]
    public void A_successful_read_still_says_the_bid_is_a_snapshot()
    {
        var read = WhatnotShowParser.Read(ShowUrl, PagesRouterShow);

        Assert.Equal(WhatnotReadStatuses.Read, read.Status);
        Assert.Contains("snapshot", read.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("under your hand", read.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Nothing_read_here_is_a_price_this_app_stands_behind()
    {
        // The read produces a title and a starting number. Everything about what the lot is WORTH
        // comes from /api/whatsnot/bid, off eBay sold comps — one ceiling, one function.
        var read = WhatnotShowParser.Read(ShowUrl, PagesRouterShow);

        Assert.Null(read.GetType().GetProperty("MaxBid"));
        Assert.Null(read.GetType().GetProperty("ResalePrice"));
    }

    // ── Not spending the seller's seconds ────────────────────────────────────────────────────

    [Fact]
    public void A_page_full_of_objects_is_read_within_its_caps()
    {
        // A live screen's budget is seconds, and the page comes from somewhere this app does not
        // control. The caps are the reason a hostile or merely enormous payload can't take the
        // screen down with it.
        var noise = string.Join(",", Enumerable.Range(0, 4000).Select(i => $$"""{"k":{{i}},"name":"noise {{i}}"}"""));
        var html = """
            <script type="application/json">{"junk":[NOISE],
            "activeListing":{"title":"Bitmain Antminer S19j Pro 104TH","price":245}}</script>
            """.Replace("NOISE", noise);

        var started = DateTime.UtcNow;
        var read = WhatnotShowParser.Read(ShowUrl, html);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5),
            "reading a show page has to fit inside the seconds a live lot lasts");
        Assert.Equal("Bitmain Antminer S19j Pro 104TH", read.Title);
    }
}
