using System.Text.Json;
using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;
using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The lookup that decides whether to spend a Terapeak scrape, and what a scrape's numbers mean.
///
/// Two separate jobs, both untested until now, both of them the kind that fail silently. The first
/// is rationing: a scrape drives a real browser against eBay under the seller's own account, so this
/// class must never initiate one on its own and must answer from the cache whenever it can. The
/// second is honesty about failure — the estimator treats a null lookup as "this source contributed
/// nothing", which is right, but only because the outcome beside it says whether Terapeak had
/// nothing to say about the ITEM or simply never answered. Collapsing those two was how a dead
/// session turned into a confident price with nothing on screen admitting the second source was gone.
///
/// Nothing here launches a browser. Every path below is reachable from a temp session file, a temp
/// SQLite database and a string.
/// </summary>
[Collection(PooledSqliteTests.Name)]
public class TerapeakMarketServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ing-tpmarket-" + Guid.NewGuid().ToString("N"));

    public TerapeakMarketServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static readonly TimeSpan Forever = TimeSpan.FromDays(3650);

    private string SessionPath => Path.Combine(_root, "terapeak-session.json");

    private TerapeakPriceCache Cache() => new(new ListingDatabase(new StubWebHostEnvironment { ContentRootPath = _root }));

    /// <summary>
    /// A service wired to a real cache and a real TerapeakService pointed at this test's temp session
    /// path — so the session state is whatever the test wrote there, and no scrape is ever reachable.
    /// </summary>
    private TerapeakMarketService Service(TerapeakPriceCache? cache = null)
    {
        var log = new ActionLog();
        return new TerapeakMarketService(
            new TerapeakService(SessionPath, Path.Combine(_root, "profile"), log),
            cache ?? Cache(),
            log);
    }

    /// <summary>A storageState shaped like Playwright's, carrying eBay cookies.</summary>
    private void WriteValidSession() => File.WriteAllText(SessionPath, JsonSerializer.Serialize(new
    {
        cookies = new[]
        {
            new { name = "dp1", value = "bpbf/#0000", domain = ".ebay.com", path = "/", expires = -1.0 },
        },
        origins = Array.Empty<object>(),
    }));

    private void WriteExpiredSession()
    {
        WriteValidSession();
        TerapeakSessionFile.MarkExpired(SessionPath, "The research page answered with a sign-in form.");
    }

    private void Backdate(string queryKey, TimeSpan age)
    {
        var db = new ListingDatabase(new StubWebHostEnvironment { ContentRootPath = _root });
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db.DatabasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE terapeak_price_cache SET scraped_at_utc = @at WHERE query_key = @key;";
        command.Parameters.AddWithValue("@at", DateTime.UtcNow.Subtract(age).ToString("O"));
        command.Parameters.AddWithValue("@key", queryKey);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static NormalizedProduct Antminer() => new()
    {
        Brand = "Bitmain", Model = "Antminer S19", Capacity = "95TH",
        Condition = "Used", Category = "Mining Hardware", RawText = "Bitmain Antminer S19 95TH used",
    };

    // ══ The cache key ════════════════════════════════════════════════════════

    [Fact]
    public void The_key_is_the_identifying_fields_lowercased_and_joined()
    {
        Assert.Equal(
            "bitmain|antminer s19|95th|used|mining hardware",
            TerapeakMarketService.BuildCacheKey(Antminer()));
    }

    [Fact]
    public void Fields_the_normalizer_could_not_fill_are_left_out_rather_than_left_blank()
    {
        var key = TerapeakMarketService.BuildCacheKey(new NormalizedProduct
        {
            Brand = "Bitmain", Model = null, Capacity = "   ", Generation = "",
            Condition = "Used", Category = "Mining Hardware", RawText = "whatever",
        });

        Assert.Equal("bitmain|used|mining hardware", key);
        Assert.DoesNotContain("||", key);
    }

    /// <summary>
    /// The reason this is a signature rather than the raw query: two sellers describing the same
    /// machine differently must land on one cache entry, or the app pays for the same scrape twice.
    /// </summary>
    [Fact]
    public void Two_wordings_of_the_same_machine_share_one_entry_and_one_scrape()
    {
        var listed = new NormalizedProduct
        {
            Brand = "BITMAIN", Model = "  Antminer S19 ", Capacity = "95TH",
            Condition = "Used", Category = "Mining Hardware",
            RawText = "*** BITMAIN ANTMINER S19 95TH/s *** WORKING PULL",
        };

        Assert.Equal(TerapeakMarketService.BuildCacheKey(Antminer()), TerapeakMarketService.BuildCacheKey(listed));
    }

    /// <summary>
    /// Condition is part of the key on purpose. A used S19 and a new-in-box one are different markets,
    /// and sharing a cached price between them is a several-hundred-dollar error in whichever
    /// direction the first scrape happened to run.
    /// </summary>
    [Fact]
    public void Condition_splits_the_entry_because_used_and_new_are_different_markets()
    {
        var used = Antminer();
        var boxed = Antminer();
        boxed.Condition = "New";

        Assert.NotEqual(TerapeakMarketService.BuildCacheKey(used), TerapeakMarketService.BuildCacheKey(boxed));
    }

    [Fact]
    public void Capacity_splits_the_entry_too()
    {
        var ninetyFive = Antminer();
        var oneTen = Antminer();
        oneTen.Capacity = "110TH";

        Assert.NotEqual(TerapeakMarketService.BuildCacheKey(ninetyFive), TerapeakMarketService.BuildCacheKey(oneTen));
    }

    /// <summary>
    /// When the normalizer recognised nothing, the key falls back to the scrubbed raw title — which
    /// still beats the raw string, because punctuation and shouting would otherwise make every ad for
    /// one item its own scrape.
    /// </summary>
    [Fact]
    public void An_unrecognised_product_keys_on_its_scrubbed_title()
    {
        var key = TerapeakMarketService.BuildCacheKey(new NormalizedProduct
        {
            RawText = "***Vintage  Widget!!! (READ)***",
        });

        Assert.Equal("vintage widget read", key);
    }

    [Fact]
    public void The_fallback_key_still_keeps_word_order_so_it_can_never_merge_two_products()
    {
        var forward = TerapeakMarketService.BuildCacheKey(new NormalizedProduct { RawText = "antminer s19" });
        var reversed = TerapeakMarketService.BuildCacheKey(new NormalizedProduct { RawText = "s19 antminer" });

        Assert.NotEqual(forward, reversed);
    }

    /// <summary>
    /// A product with no fields and no title produces an empty key, and the cache would happily share
    /// one entry between every such product. Nothing can be said about an item this empty anyway — but
    /// pinned, because "" is a key like any other in the table and a future field-stripping change
    /// could route real products through it.
    /// </summary>
    [Fact]
    public void A_product_with_nothing_in_it_produces_an_empty_key()
    {
        Assert.Equal("", TerapeakMarketService.BuildCacheKey(new NormalizedProduct()));
    }

    // ══ Reading the research page ════════════════════════════════════════════

    // Shaped exactly like the Seller Hub Research page's innerText: each stat tile renders as
    // "<value>\n<label>", each result row as "<price>\n<format>".
    private const string ResearchPage = """
        Sold items
        $64.31
        Avg sold price
        $41.00 - $92.50
        Sold price range
        72%
        Sell-through
        $8.45
        Avg shipping
        Results
        Bitmain Antminer S19 95TH Bitcoin Miner
        $61.00
        Fixed price
        Bitmain Antminer S19 95TH/s ASIC
        $70.00
        Auction
        Antminer S19 95T Miner w/ PSU
        $55.00
        Best Offer
        """;

    [Fact]
    public void Every_number_on_the_research_page_is_read()
    {
        var parsed = TerapeakMarketService.ParseTerapeakBodyText(ResearchPage, "antminer s19 95th")!;

        Assert.Equal("antminer s19 95th", parsed.Query);
        Assert.Equal(64.31m, parsed.Average);
        Assert.Equal(41.00m, parsed.Min);
        Assert.Equal(92.50m, parsed.Max);
        Assert.Equal(72m, parsed.SellThroughPercent);
        Assert.Equal(8.45m, parsed.AvgShipping);
        Assert.Equal(3, parsed.Count);
    }

    /// <summary>
    /// The median is computed from the per-listing rows, not taken from the page — the page only
    /// offers a single blended average, and an average is what one $4,000 outlier moves and a median
    /// is what it doesn't. All three listing formats are real sales and all three count.
    /// </summary>
    [Fact]
    public void The_median_comes_from_the_rows_and_all_three_listing_formats_count()
    {
        var parsed = TerapeakMarketService.ParseTerapeakBodyText(ResearchPage, "q")!;

        Assert.Equal(3, parsed.Count);
        Assert.Equal(61.00m, parsed.Median);
    }

    [Fact]
    public void Rows_are_sorted_before_the_middle_is_taken()
    {
        var page = """
            $900.00
            Fixed price
            $100.00
            Fixed price
            $300.00
            Fixed price
            """;

        Assert.Equal(300m, TerapeakMarketService.ParseTerapeakBodyText(page, "q")!.Median);
    }

    [Fact]
    public void An_even_number_of_rows_takes_the_midpoint_of_the_middle_two()
    {
        var page = """
            $100.00
            Fixed price
            $200.00
            Auction
            $300.00
            Fixed price
            $500.00
            Best Offer
            """;

        var parsed = TerapeakMarketService.ParseTerapeakBodyText(page, "q")!;

        Assert.Equal(4, parsed.Count);
        Assert.Equal(250m, parsed.Median);
    }

    /// <summary>
    /// Four-figure comps are where the money is, and they arrive with a thousands separator. Reading
    /// "$1,150.00" as 1.15 or as 115000 is the difference between a flip and a fortune.
    /// </summary>
    [Fact]
    public void Thousands_separators_do_not_move_the_decimal_point()
    {
        var page = """
            $1,150.40
            Avg sold price
            $1,099.00 - $2,400.00
            Sold price range
            $1,099.00
            Fixed price
            """;

        var parsed = TerapeakMarketService.ParseTerapeakBodyText(page, "q")!;

        Assert.Equal(1150.40m, parsed.Average);
        Assert.Equal(1099.00m, parsed.Min);
        Assert.Equal(2400.00m, parsed.Max);
        Assert.Equal(1099.00m, parsed.Median);
    }

    /// <summary>
    /// Terapeak prints "-" instead of a percentage when it can't compute one. Reading that as 0%
    /// would mark an item nobody measured as an item nobody buys, and every score built on
    /// sell-through would inherit it.
    /// </summary>
    [Fact]
    public void A_sell_through_the_page_could_not_compute_stays_unknown_rather_than_zero()
    {
        var page = """
            $64.31
            Avg sold price
            -
            Sell-through
            $61.00
            Fixed price
            """;

        Assert.Null(TerapeakMarketService.ParseTerapeakBodyText(page, "q")!.SellThroughPercent);
    }

    [Fact]
    public void A_page_with_no_shipping_tile_reports_no_shipping_cost_rather_than_failing()
    {
        var page = """
            $64.31
            Avg sold price
            $61.00
            Fixed price
            """;

        var parsed = TerapeakMarketService.ParseTerapeakBodyText(page, "q")!;

        Assert.Equal(0m, parsed.AvgShipping);
        Assert.Equal(64.31m, parsed.Average);
    }

    /// <summary>
    /// THE assertion the caching guard rests on. A sign-in wall, a bot challenge or a subscription
    /// page has no comps on it by definition — if any of them parsed into a result, that result would
    /// be written to a cache that never evicts, and the bogus price would be served for that keyword
    /// forever.
    /// </summary>
    [Theory]
    [InlineData("Sign in to your eBay account\nEmail or username\nPassword\nStay signed in")]
    [InlineData("Please verify yourself to continue\nSecurity Measure\nWe noticed unusual activity")]
    [InlineData("Subscribe to Terapeak Product Research\nStart your subscription")]
    [InlineData("")]
    [InlineData("   \n \n  ")]
    public void A_page_this_app_was_bounced_off_parses_to_nothing_at_all(string page)
    {
        Assert.Null(TerapeakMarketService.ParseTerapeakBodyText(page, "antminer s19"));
    }

    /// <summary>
    /// A genuinely empty result set is also nothing — "0 results" is Terapeak saying it has no sold
    /// history, and the caller turns that into NoData rather than into a price of zero.
    /// </summary>
    [Fact]
    public void An_empty_result_set_parses_to_nothing_rather_than_to_a_price_of_zero()
    {
        Assert.Null(TerapeakMarketService.ParseTerapeakBodyText(
            "0 results found\nTry a different keyword\nSell-through\nAvg sold price", "q"));
    }

    [Fact]
    public void A_summary_with_no_rows_is_still_a_usable_price()
    {
        var parsed = TerapeakMarketService.ParseTerapeakBodyText("$64.31\nAvg sold price", "q")!;

        Assert.Equal(64.31m, parsed.Average);
        Assert.Equal(0, parsed.Count);
        Assert.Equal(0m, parsed.Median);
    }

    [Fact]
    public void Rows_with_no_summary_are_still_a_usable_price()
    {
        var parsed = TerapeakMarketService.ParseTerapeakBodyText("$61.00\nFixed price\n$70.00\nAuction", "q")!;

        Assert.Equal(0m, parsed.Average);
        Assert.Equal(2, parsed.Count);
        Assert.Equal(65.50m, parsed.Median);
    }

    /// <summary>
    /// Only prices labelled with a listing format are sold comps. The page is full of other dollar
    /// amounts — shipping quotes, promoted-listing budgets, unsold asking prices — and counting any
    /// of them as a sale drags the median toward numbers nobody paid.
    /// </summary>
    [Fact]
    public void Dollar_amounts_that_are_not_sales_are_not_counted_as_sales()
    {
        var page = """
            $5.00
            Shipping to 89101
            $61.00
            Fixed price
            $12.99
            Promoted listing budget
            """;

        var parsed = TerapeakMarketService.ParseTerapeakBodyText(page, "q")!;

        Assert.Equal(1, parsed.Count);
        Assert.Equal(61.00m, parsed.Median);
    }

    // ══ Rationing the browser ════════════════════════════════════════════════

    [Fact]
    public async Task A_cached_price_is_served_without_any_of_this_touching_a_browser()
    {
        var cache = Cache();
        cache.Set(TerapeakMarketService.BuildCacheKey(Antminer()), 1150.40m, 1099m, 84.25m, 72m);

        var lookup = await Service(cache).LookupAsync(Antminer(), "Bitmain Antminer S19 95TH", allowRealScrape: false);

        Assert.Equal(TerapeakOutcome.Cached, lookup.Outcome);
        Assert.True(lookup.HasData);
        Assert.True(lookup.Result!.FromCache);
        Assert.Equal(1150.40m, lookup.Result.Data.Average);
        Assert.Equal(1099m, lookup.Result.Data.Median);
        Assert.Equal(84.25m, lookup.Result.Data.AvgShipping);
        Assert.Equal(72m, lookup.Result.Data.SellThroughPercent);
    }

    /// <summary>
    /// A served price arrives with the evidence behind it, not just the number: how many sales the
    /// median came from and how widely they ranged.
    ///
    /// This is the difference between a second opinion and a decoration. Everything downstream
    /// weighs a source by its sample size and its spread — <see cref="MarketPriceEstimator"/> gives
    /// a source with no count a weight of zero — and this path is the one nearly every lookup takes,
    /// because a scan's whole job is to answer from the cache rather than drive a browser. A count
    /// dropped here is Terapeak dropped from the price, quietly, on almost every row.
    /// </summary>
    [Fact]
    public async Task A_cached_price_is_served_with_the_sample_size_that_earns_it_its_weight()
    {
        var cache = Cache();
        cache.Set(TerapeakMarketService.BuildCacheKey(Antminer()), 1150.40m, 1099m, 84.25m, 72m,
            compCount: 14, min: 880m, max: 1425.50m);

        var lookup = await Service(cache).LookupAsync(Antminer(), "Bitmain Antminer S19 95TH", allowRealScrape: false);

        Assert.Equal(14, lookup.Result!.Data.Count);
        Assert.Equal(880m, lookup.Result.Data.Min);
        Assert.Equal(1425.50m, lookup.Result.Data.Max);
    }

    /// <summary>
    /// The rows already in the database were written before the count was stored, and they come back
    /// saying so. Zero here means "unrecorded" and has to keep behaving the old way — inventing a
    /// sample size for a price whose sample size nobody kept would put weight on nothing at all.
    /// </summary>
    [Fact]
    public async Task A_price_cached_without_a_sample_size_does_not_pretend_to_have_one()
    {
        var cache = Cache();
        cache.Set(TerapeakMarketService.BuildCacheKey(Antminer()), 1150m, 1099m, 84m, 72m);

        var lookup = await Service(cache).LookupAsync(Antminer(), "q", allowRealScrape: false);

        Assert.Equal(1099m, lookup.Result!.Data.Median);
        Assert.Equal(0, lookup.Result.Data.Count);
    }

    /// <summary>
    /// The result carries the seller's own query back, not the normalized key it was stored under —
    /// the key is an implementation detail and putting it on screen reads as the app having searched
    /// for something the seller never typed.
    /// </summary>
    [Fact]
    public async Task The_served_result_names_the_search_the_seller_asked_for()
    {
        var cache = Cache();
        cache.Set(TerapeakMarketService.BuildCacheKey(Antminer()), 1150m, 1099m, 84m, 72m);

        var lookup = await Service(cache).LookupAsync(Antminer(), "Bitmain Antminer S19 95TH", allowRealScrape: false);

        Assert.Equal("Bitmain Antminer S19 95TH", lookup.Result!.Data.Query);
    }

    /// <summary>
    /// A cached price is served even when the saved session is dead. The two are unrelated — the
    /// price was already paid for — and making a broken login suppress prices the app already owns
    /// would blank the whole board the moment eBay logged the seller out.
    /// </summary>
    [Fact]
    public async Task A_dead_session_does_not_withhold_prices_already_paid_for()
    {
        WriteExpiredSession();
        var cache = Cache();
        cache.Set(TerapeakMarketService.BuildCacheKey(Antminer()), 1150m, 1099m, 84m, 72m);

        var lookup = await Service(cache).LookupAsync(Antminer(), "Bitmain Antminer S19 95TH", allowRealScrape: true);

        Assert.Equal(TerapeakOutcome.Cached, lookup.Outcome);
        Assert.False(lookup.NeedsReconnect);
    }

    /// <summary>
    /// The default window is 48 hours. Callers that want a wider one say so; callers that say nothing
    /// get a price no more than two days old.
    /// </summary>
    [Fact]
    public async Task Prices_up_to_two_days_old_are_served_by_default_and_older_ones_are_not()
    {
        var key = TerapeakMarketService.BuildCacheKey(Antminer());
        var cache = Cache();
        cache.Set(key, 1150m, 1099m, 84m, 72m);
        Backdate(key, TimeSpan.FromHours(47));

        Assert.Equal(TerapeakOutcome.Cached,
            (await Service(cache).LookupAsync(Antminer(), "q", allowRealScrape: false)).Outcome);

        Backdate(key, TimeSpan.FromHours(49));

        Assert.Equal(TerapeakOutcome.NotAttempted,
            (await Service(cache).LookupAsync(Antminer(), "q", allowRealScrape: false)).Outcome);
    }

    [Fact]
    public async Task A_caller_willing_to_accept_an_older_price_gets_one()
    {
        var key = TerapeakMarketService.BuildCacheKey(Antminer());
        var cache = Cache();
        cache.Set(key, 1150m, 1099m, 84m, 72m);
        Backdate(key, TimeSpan.FromDays(120));

        var lookup = await Service(cache).LookupAsync(Antminer(), "q", allowRealScrape: false, maxAge: Forever);

        Assert.Equal(TerapeakOutcome.Cached, lookup.Outcome);
    }

    /// <summary>
    /// An old price is still a price, but it must not carry the same weight as a fresh one — a
    /// six-month-old comp on a depreciating miner is evidence, not an answer.
    /// </summary>
    [Theory]
    [InlineData(1, 1.0)]
    [InlineData(30, 1.0)]
    [InlineData(31, 0.7)]
    [InlineData(90, 0.7)]
    [InlineData(91, 0.4)]
    [InlineData(180, 0.4)]
    [InlineData(181, 0.2)]
    [InlineData(900, 0.2)]
    public async Task An_older_price_carries_less_weight(int ageDays, double expectedWeight)
    {
        var key = TerapeakMarketService.BuildCacheKey(Antminer());
        var cache = Cache();
        cache.Set(key, 1150m, 1099m, 84m, 72m);
        // A minute inside the boundary, so a test that runs across a tick still lands in its bucket.
        Backdate(key, TimeSpan.FromDays(ageDays) - TimeSpan.FromMinutes(1));

        var lookup = await Service(cache).LookupAsync(Antminer(), "q", allowRealScrape: false, maxAge: Forever);

        Assert.Equal(expectedWeight, lookup.Result!.FreshnessWeight);
    }

    [Fact]
    public async Task A_served_price_is_dated_when_it_was_scraped_not_when_it_was_read()
    {
        var key = TerapeakMarketService.BuildCacheKey(Antminer());
        var cache = Cache();
        cache.Set(key, 1150m, 1099m, 84m, 72m);
        Backdate(key, TimeSpan.FromDays(45));

        var lookup = await Service(cache).LookupAsync(Antminer(), "q", allowRealScrape: false, maxAge: Forever);

        Assert.InRange((DateTime.UtcNow - lookup.Result!.ScrapedAtUtc).TotalDays, 44.9, 45.1);
    }

    // ══ Not answering, and saying which kind ═════════════════════════════════

    /// <summary>
    /// The scan's free pre-pass: every product is looked up from the cache before a single scrape is
    /// spent. A miss here is "not checked", which is not a fact about the item.
    /// </summary>
    [Fact]
    public async Task A_lookup_that_was_not_authorised_to_scrape_says_so_and_scrapes_nothing()
    {
        WriteValidSession();

        var lookup = await Service().LookupAsync(Antminer(), "Bitmain Antminer S19 95TH", allowRealScrape: false);

        Assert.Equal(TerapeakOutcome.NotAttempted, lookup.Outcome);
        Assert.False(lookup.HasData);
        Assert.False(lookup.IsConnectionProblem);
        Assert.False(lookup.NeedsReconnect);
        Assert.NotEqual("", lookup.Reason);
    }

    /// <summary>
    /// Authorisation is checked before the session file is, because it is free. A scan runs this over
    /// several hundred products before spending anything, and stat-ing a session file per product to
    /// reach the same "no" is work nobody asked for.
    /// </summary>
    [Fact]
    public async Task An_unauthorised_lookup_does_not_even_look_at_the_session()
    {
        Assert.False(File.Exists(SessionPath));

        var lookup = await Service().LookupAsync(Antminer(), "q", allowRealScrape: false);

        Assert.Equal(TerapeakOutcome.NotAttempted, lookup.Outcome);
        Assert.NotEqual(TerapeakOutcome.NotConnected, lookup.Outcome);
    }

    /// <summary>
    /// The failure this whole outcome enum was added for. eBay stopped accepting the saved session,
    /// so the answer is a Reconnect button and the seller's own sentence about what happened — not a
    /// silently missing second opinion behind a confident price.
    /// </summary>
    [Fact]
    public async Task A_session_eBay_stopped_accepting_asks_for_a_reconnect()
    {
        WriteExpiredSession();

        var lookup = await Service().LookupAsync(Antminer(), "Bitmain Antminer S19 95TH", allowRealScrape: true);

        Assert.Equal(TerapeakOutcome.SessionExpired, lookup.Outcome);
        Assert.False(lookup.HasData);
        Assert.True(lookup.NeedsReconnect);
        Assert.True(lookup.IsConnectionProblem);
        Assert.Contains("Reconnect Terapeak in Settings", lookup.Reason);
        Assert.Contains("sign-in page", lookup.Reason);
    }

    [Fact]
    public async Task A_login_that_was_never_made_asks_for_a_connection_not_a_reconnection()
    {
        var lookup = await Service().LookupAsync(Antminer(), "Bitmain Antminer S19 95TH", allowRealScrape: true);

        Assert.Equal(TerapeakOutcome.NotConnected, lookup.Outcome);
        Assert.False(lookup.HasData);
        Assert.False(lookup.NeedsReconnect);
        Assert.True(lookup.IsConnectionProblem);
        Assert.Contains("isn't connected", lookup.Reason);
    }

    /// <summary>
    /// A session file truncated by a crash is not a session eBay refused, so it gets Connect rather
    /// than Reconnect — nothing here should tell the seller eBay rejected a login when the file never
    /// held one.
    /// </summary>
    [Fact]
    public async Task A_session_file_wrecked_by_a_crash_is_not_reported_as_a_refused_login()
    {
        File.WriteAllText(SessionPath, "{\"cookies\": [{\"name\": \"dp1\"");

        var lookup = await Service().LookupAsync(Antminer(), "q", allowRealScrape: true);

        Assert.Equal(TerapeakOutcome.NotConnected, lookup.Outcome);
        Assert.False(lookup.NeedsReconnect);
    }

    /// <summary>
    /// The distinction the enum exists to keep. None of the ways Terapeak can fail to answer is
    /// NoData, because NoData is a claim about the ITEM — "this doesn't sell" — and printing it on
    /// the strength of a broken connection is how the board tells a seller to pass on a good flip.
    /// </summary>
    [Fact]
    public async Task No_failure_to_reach_Terapeak_is_ever_reported_as_the_item_having_no_sales()
    {
        var outcomes = new List<TerapeakOutcome>();

        outcomes.Add((await Service().LookupAsync(Antminer(), "q", allowRealScrape: false)).Outcome);
        outcomes.Add((await Service().LookupAsync(Antminer(), "q", allowRealScrape: true)).Outcome);

        WriteExpiredSession();
        outcomes.Add((await Service().LookupAsync(Antminer(), "q", allowRealScrape: true)).Outcome);

        Assert.Equal([TerapeakOutcome.NotAttempted, TerapeakOutcome.NotConnected, TerapeakOutcome.SessionExpired], outcomes);
        Assert.DoesNotContain(TerapeakOutcome.NoData, outcomes);
    }

    /// <summary>
    /// GetAsync is the shape the pricing maths wants: every failure is null, so a source that did not
    /// answer contributes nothing to a blend rather than contributing a zero.
    /// </summary>
    [Fact]
    public async Task The_numbers_only_overload_answers_every_failure_with_null()
    {
        WriteExpiredSession();

        Assert.Null(await Service().GetAsync(Antminer(), "q", allowRealScrape: false));
        Assert.Null(await Service().GetAsync(Antminer(), "q", allowRealScrape: true));
    }

    [Fact]
    public async Task The_numbers_only_overload_still_serves_a_cached_price()
    {
        var cache = Cache();
        cache.Set(TerapeakMarketService.BuildCacheKey(Antminer()), 1150m, 1099m, 84m, 72m);

        var result = await Service(cache).GetAsync(Antminer(), "q", allowRealScrape: false);

        Assert.NotNull(result);
        Assert.Equal(1150m, result.Data.Average);
    }

    /// <summary>
    /// A failed lookup must not be charged to the cache ledger in either column: it neither drove a
    /// browser nor saved one, and counting it would make the ratio that reports "how much real eBay
    /// traffic is this app generating" wrong in the direction that hides a problem.
    /// </summary>
    [Fact]
    public async Task A_lookup_that_never_ran_costs_the_scrape_ledger_nothing()
    {
        WriteExpiredSession();
        var cache = Cache();

        await Service(cache).LookupAsync(Antminer(), "q", allowRealScrape: true);
        await Service(cache).LookupAsync(Antminer(), "q", allowRealScrape: false);

        Assert.Equal((0, 0), cache.GetStats());
        Assert.Equal(0, cache.Count);
    }
}
