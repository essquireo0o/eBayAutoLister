using System.Net;
using System.Text;
using System.Text.Json;
using ING_eBay_AutoLister.Services;
using Microsoft.Extensions.Configuration;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// Live sold-price lookups over the OpenWebNinja API, and the budget standing in front of them.
/// </summary>
/// <remarks>
/// <para>
/// Two things are being protected here and they pull in opposite directions. The first is that a
/// lookup must actually answer with real rows, on a server as well as on a desktop — that is the
/// whole reason the browser scraper was replaced, and it is why <c>app.inglisting.com</c> priced
/// from stale stored comps for months. The second is that every one of those answers costs a call
/// out of a paid, finite 50,000 that does not refill, so the interesting tests are the ones about
/// calls NOT being made: the 24-hour cache, the per-account daily cap, and the kill switch.
/// </para>
/// <para>
/// The normalising tests look small and are not. <c>"Free shipping"</c> read as unknown, or a sold
/// date that fails to parse, do not break anything visibly — they quietly understate every comp
/// they touch, on a number a seller is about to price real money against.
/// </para>
/// <para>
/// In <see cref="PooledSqliteTests"/>'s collection because these hold pooled SQLite connections to
/// their own scratch databases, and a class in another collection calling
/// <c>ClearAllPools()</c> disposes them mid-use — which surfaces here as an
/// <c>ObjectDisposedException</c> from somewhere that has nothing to do with the cause, and only
/// when the scheduler happens to overlap the two.
/// </para>
/// </remarks>
[Collection(PooledSqliteTests.Name)]
public class LiveCompsTests
{
    // ── Normalising what the API sends ───────────────────────────────────────────────────────

    [Fact]
    public void Free_shipping_is_zero_and_unknown_shipping_stays_unknown()
    {
        // Free delivery is part of what the buyer paid: it is 0.00, not "we don't know".
        Assert.Equal(0m, OpenWebNinjaClient.ShippingOf(Item("""{"shipping":"Free shipping"}""")));
        Assert.Equal(0m, OpenWebNinjaClient.ShippingOf(Item("""{"shipping":"Free 2-4 day delivery"}""")));

        // And a real cost is the number inside the sentence the API actually sends.
        Assert.Equal(49.99m, OpenWebNinjaClient.ShippingOf(Item("""{"shipping":"+$49.99 delivery"}""")));

        // Unknown must NOT become zero. Treating it as free understates the comp.
        Assert.Null(OpenWebNinjaClient.ShippingOf(Item("""{"shipping":null}""")));
        Assert.Null(OpenWebNinjaClient.ShippingOf(Item("""{}""")));
    }

    [Fact]
    public void The_sold_date_is_read_out_of_the_caption()
    {
        Assert.Equal("2026-08-13", OpenWebNinjaClient.SoldDateOf(Item("""{"caption":"Sold Aug 13, 2026"}""")));
        Assert.Equal("2026-08-03", OpenWebNinjaClient.SoldDateOf(Item("""{"caption":"Sold Aug 3, 2026"}""")));
        Assert.Equal("2026-08-13", OpenWebNinjaClient.SoldDateOf(Item("""{"caption":"Sold August 13, 2026"}""")));

        // A caption that is not a sale date is not a date. Every freshness weight in the pricing
        // path is computed off this field, so inventing one would age comps wrongly in silence.
        Assert.Equal("", OpenWebNinjaClient.SoldDateOf(Item("""{"caption":"Almost gone"}""")));
        Assert.Equal("", OpenWebNinjaClient.SoldDateOf(Item("""{}""")));
    }

    [Fact]
    public void Prices_arrive_as_numbers_or_as_money_text()
    {
        Assert.Equal(139.99m, OpenWebNinjaClient.Money("139.99"));
        Assert.Equal(1234.56m, OpenWebNinjaClient.Money("$1,234.56"));
        Assert.Null(OpenWebNinjaClient.Money(""));
        Assert.Null(OpenWebNinjaClient.Money("Best offer"));
    }

    [Fact]
    public void A_page_of_sold_items_becomes_rows_the_pricing_path_can_read()
    {
        var fetch = OpenWebNinjaClient.Parse(PageWith(2), 200);

        Assert.True(fetch.Ok);
        Assert.Equal(2, fetch.Rows.Count);

        var first = fetch.Rows[0];
        Assert.Equal("100000000001", first.ItemId);
        Assert.Equal("Bitmain Antminer S19j Pro 104TH Bitcoin Miner", first.Title);
        Assert.Equal(139.99m, first.Price);
        Assert.Equal(49.99m, first.Shipping);
        Assert.Equal("Pre-Owned", first.Condition);
        Assert.Equal("2026-08-13", first.SoldDate);

        // The untouched item is kept, so a later parser can take fields this one ignored without
        // spending the call again.
        Assert.Contains("\"buying_format\"", first.RawJson, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_with_no_item_id_is_dropped_rather_than_double_counted()
    {
        // ItemId is the only thing that dedupes a sale against the ones already stored, and one
        // sale counted five times is a market that does not exist.
        var fetch = OpenWebNinjaClient.Parse("""
            {"data":{"total_results":1,"products":[{"title":"No id here","price":10}]}}
            """, 200);

        Assert.True(fetch.Ok);
        Assert.Empty(fetch.Rows);
    }

    [Fact]
    public void An_answer_that_is_not_the_expected_shape_is_a_failure_not_an_empty_market()
    {
        Assert.False(OpenWebNinjaClient.Parse("<html>gateway timeout</html>", 200).Ok);
        Assert.False(OpenWebNinjaClient.Parse("""{"status":"error"}""", 200).Ok);
    }

    // ── The lookup, end to end ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_lookup_that_answers_stores_its_rows_and_reports_ok()
    {
        using var world = new World();

        var run = world.Run("antminer s19");

        Assert.Equal("ok", run.Outcome);
        Assert.Equal(2, run.RowsFound);
        Assert.Equal(2, run.RowsNew);
        Assert.False(run.SourceFailed);
        Assert.Equal(1, world.Handler.Calls);

        // And the rows are in the table the pricing path reads — which is the entire point of
        // making somebody wait for the lookup.
        Assert.Equal(2, world.Store.StoredRowCount("antminer s19"));
    }

    [Fact]
    public void The_same_model_again_is_served_from_cache_without_spending_a_call()
    {
        using var world = new World();

        var first = world.Run("antminer s19");
        Assert.Equal("ok", first.Outcome);
        Assert.Equal(1, world.Handler.Calls);

        // Nine hours later, same model — and typed with different capitalisation, which is what a
        // second seller actually does.
        world.Now = world.Now.AddHours(9);
        var second = world.Lookup.Start("Antminer S19");

        Assert.True(second.Finished);
        Assert.Equal("fresh", second.Outcome);
        Assert.Equal(1, world.Handler.Calls);          // no second call was made
        Assert.Equal(1, world.Usage.Used(UserScope.DesktopOwner, LiveCompsUsageStore.DayOf(world.Now)));

        // It still reports rows, because there are rows — the stored ones for that model.
        Assert.Equal(2, second.RowsFound);

        // Past the window it is worth asking again.
        world.Now = world.Now.AddHours(16);
        var later = world.Run("antminer s19");
        Assert.Equal("ok", later.Outcome);
        Assert.Equal(2, world.Handler.Calls);
    }

    [Fact]
    public void An_api_error_is_an_error_and_never_no_recent_sales()
    {
        // The distinction this whole path exists to protect: zero rows because the source failed,
        // and zero rows because the model does not sell, are opposite advice.
        using var world = new World(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("upstream exploded"),
        });

        var run = world.Run("antminer s19");

        Assert.Equal("error", run.Outcome);
        Assert.True(run.SourceFailed);
        Assert.Equal(0, run.RowsFound);
        Assert.DoesNotContain("no recent", run.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_empty_result_is_empty_and_says_so()
    {
        using var world = new World(_ => Json("""{"data":{"total_results":0,"products":[]}}"""));

        var run = world.Run("a model nobody has ever sold");

        Assert.Equal("empty", run.Outcome);
        Assert.False(run.SourceFailed);              // this is information, not a failure
        Assert.Equal(0, run.RowsFound);

        // And it is cached like any other answer: "nothing sold" does not change within a day, and
        // asking again would spend a call to be told the same thing.
        world.Now = world.Now.AddHours(2);
        Assert.Equal("fresh", world.Lookup.Start("a model nobody has ever sold").Outcome);
        Assert.Equal(1, world.Handler.Calls);
    }

    // ── The budget ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_lookup_after_todays_allowance_is_refused_before_any_call_is_made()
    {
        using var world = new World { DailyLimit = 3 };

        for (var i = 1; i <= 3; i++)
            Assert.Equal("ok", world.Run($"model number {i}").Outcome);

        var refused = world.Lookup.Start("model number 4");

        Assert.Equal("rate_limited", refused.Outcome);
        Assert.True(refused.Finished);
        Assert.Equal(3, world.Handler.Calls);                 // the fourth never reached the API
        Assert.Contains("stored comps", refused.Message, StringComparison.OrdinalIgnoreCase);

        // Tomorrow it works again, and the seller was told when that would be.
        Assert.Contains("UTC", refused.Message, StringComparison.Ordinal);
        world.Now = world.Now.AddDays(1);
        Assert.Equal("ok", world.Run("model number 4").Outcome);
        Assert.Equal(4, world.Handler.Calls);
    }

    [Fact]
    public void A_failed_call_still_costs_the_allowance_because_the_api_still_billed_it()
    {
        using var world = new World(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("slow down"),
        })
        { DailyLimit = 2 };

        Assert.Equal("error", world.Run("model one").Outcome);
        Assert.Equal("error", world.Run("model two").Outcome);
        Assert.Equal("rate_limited", world.Lookup.Start("model three").Outcome);
        Assert.Equal(2, world.Handler.Calls);
    }

    [Fact]
    public void The_kill_switch_stops_every_lookup_without_removing_the_key()
    {
        using var world = new World { Enabled = false };

        var run = world.Lookup.Start("antminer s19");

        Assert.Equal("unavailable", run.Outcome);
        Assert.True(run.Finished);
        Assert.Equal(0, world.Handler.Calls);
        Assert.False(world.Lookup.IsAvailable);
        Assert.Contains("stored comps", run.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_deployment_with_no_key_reports_unavailable_rather_than_failing()
    {
        using var world = new World { ApiKey = "" };

        var run = world.Lookup.Start("antminer s19");

        Assert.Equal("unavailable", run.Outcome);
        Assert.Equal(0, world.Handler.Calls);
    }

    [Fact]
    public void Every_call_is_recorded_so_the_spend_can_be_audited()
    {
        using var world = new World();

        world.Run("antminer s19");
        world.Run("whatsminer m30s");

        var calls = world.Usage.RecentCalls();
        Assert.Equal(2, calls.Count);
        Assert.Equal("whatsminer m30s", calls[0].Query);        // newest first
        Assert.All(calls, c => Assert.Equal("ok", c.Outcome));
        Assert.All(calls, c => Assert.Equal(200, c.HttpStatus));

        // What the bill is, from the app's own records rather than from the vendor's dashboard.
        Assert.Equal(2, world.Usage.CallsSince(world.Now.AddDays(-1)));
    }

    [Fact]
    public void The_configured_cap_is_read_but_a_typo_never_means_uncapped()
    {
        Assert.Equal(3, LiveComps.LimitFrom(Config(("LiveComps:DailyLookupLimit", "3"))));
        Assert.Equal(LiveComps.DefaultDailyLimit, LiveComps.LimitFrom(Config()));
        Assert.Equal(LiveComps.DefaultDailyLimit, LiveComps.LimitFrom(Config(("LiveComps:DailyLookupLimit", "ten"))));

        // Zero, deliberately typed, is the owner saying "do not ration me".
        Assert.Equal(0, LiveComps.LimitFrom(Config(("LiveComps:DailyLookupLimit", "0"))));

        // The kill switch is on unless it is explicitly turned off.
        Assert.True(LiveComps.EnabledFrom(Config()));
        Assert.True(LiveComps.EnabledFrom(Config(("LiveComps:Enabled", "true"))));
        Assert.False(LiveComps.EnabledFrom(Config(("LiveComps:Enabled", "false"))));
    }

    // ── The contract with everything that was already there ──────────────────────────────────

    /// <summary>
    /// The UI polls a fixed shape and the point of the change was that only the source of the rows
    /// moved. If a field name here drifts, the progress bar stops moving and the panel loses the
    /// sentence that distinguishes "no sales" from "the lookup failed".
    /// </summary>
    [Fact]
    public void The_run_shape_the_ui_polls_is_unchanged()
    {
        var program = ReadSource("Program.cs");

        Assert.Contains("app.MapPost(\"/api/comps/live/start\"", program, StringComparison.Ordinal);
        Assert.Contains("app.MapGet(\"/api/comps/live/status\"", program, StringComparison.Ordinal);

        foreach (var field in new[] { "id =", "query =", "stage =", "percent =", "rowsFound =",
                                      "rowsNew =", "finished =", "outcome =", "message =",
                                      "blockedByEbay =", "elapsedSeconds =" })
            Assert.Contains(field, program, StringComparison.Ordinal);
    }

    /// <summary>
    /// eBay's bot check cannot happen to an HTTP API, so the states that reported one are gone
    /// rather than kept alive as outcomes nothing can ever produce.
    /// </summary>
    [Fact]
    public void The_states_that_only_a_browser_could_reach_are_gone()
    {
        var lookup = ReadSource(Path.Combine("Services", "LiveCompsLookup.cs"));

        Assert.DoesNotContain("\"challenge\"", lookup, StringComparison.Ordinal);
        Assert.DoesNotContain("\"blocked\"", lookup, StringComparison.Ordinal);

        // And nothing constructs the retired scraper any more, which is what makes it dead code
        // rather than a second live path nobody is watching.
        var program = ReadSource("Program.cs");
        Assert.DoesNotContain("AddSingleton<OnDemandCompsScraper>", program, StringComparison.Ordinal);
        Assert.DoesNotContain("OnDemandCompsScraper scraper", program, StringComparison.Ordinal);
    }

    // ── Waiting for a run on the server ─────────────────────────────────────────────────────

    [Fact]
    public async Task FetchAsync_waits_for_the_lookup_and_hands_back_the_finished_run()
    {
        // The scan pipeline prices on the server and cannot poll like the browser does; this is how
        // a Facebook feed gets the live half of the sold-comps path. Same Start, same rows filed,
        // same call spent — it only waits.
        using var world = new World();

        var run = await world.Lookup.FetchAsync("antminer s19");

        Assert.True(run.Finished);
        Assert.Equal("ok", run.Outcome);
        Assert.Equal(2, run.RowsFound);
        Assert.Equal(1, world.Handler.Calls);
        Assert.Equal(2, world.Store.StoredRowCount("antminer s19"));
    }

    [Fact]
    public async Task FetchAsync_returns_a_refusal_at_once_without_spending_a_call()
    {
        using var world = new World { Enabled = false };

        var run = await world.Lookup.FetchAsync("antminer s19");

        Assert.True(run.Finished);
        Assert.Equal("unavailable", run.Outcome);
        Assert.Equal(0, world.Handler.Calls);
    }

    // ── Is the source actually answering? ────────────────────────────────────────────────────
    //
    // Added 2026-08-23, after the source spent three days returning HTTP 503 ("eBay requires an
    // authenticated session") to every call while the app happily reported itself "available" —
    // because available only ever meant a key was configured. Every product in a 120-row scan
    // spent an allowance call into the outage, and the board said "no sold data", which a seller
    // reads as "this has never sold".

    [Fact]
    public void One_bad_call_is_a_bad_minute_and_not_an_outage()
    {
        using var world = new World(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"error\":{\"message\":\"eBay requires an authenticated session\"}}"),
        });

        world.Run("antminer s19");

        // Calling a source down on a single timeout would be its own kind of lie.
        Assert.True(world.Lookup.Health.Answering);
        Assert.True(world.Lookup.ShouldAttempt);
        Assert.Equal("", world.Lookup.Health.Note);
    }

    [Fact]
    public void A_source_that_fails_repeatedly_is_reported_down_and_stops_being_paid_for()
    {
        using var world = new World(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"error\":{\"message\":\"eBay requires an authenticated session\"}}"),
        });

        for (var i = 0; i < LiveCompsLookup.FailuresBeforeDown; i++) world.Run($"antminer s19 {i}");

        var health = world.Lookup.Health;
        Assert.True(health.Configured);              // the key is fine; the source is not
        Assert.False(health.Answering);
        Assert.Equal(LiveCompsLookup.FailuresBeforeDown, health.ConsecutiveFailures);

        // And the allowance stops being spent to learn the same thing a fourth time.
        Assert.False(world.Lookup.ShouldAttempt);

        // Said in words a board can print, and saying which of the two it is.
        Assert.Contains("Live sold prices are unavailable", health.Note);
        Assert.Contains("fault at the source", health.Note);
    }

    [Fact]
    public void The_cooldown_buys_one_more_try_rather_than_writing_the_source_off()
    {
        using var world = new World(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("down"),
        });

        for (var i = 0; i < LiveCompsLookup.FailuresBeforeDown; i++) world.Run($"antminer s19 {i}");
        Assert.False(world.Lookup.ShouldAttempt);

        world.Now = world.Now.Add(LiveCompsLookup.RetryAfterDown).AddSeconds(1);

        // A circuit breaker, not a kill switch: the source must be able to come back.
        Assert.True(world.Lookup.Health.RetryDue);
        Assert.True(world.Lookup.ShouldAttempt);
    }

    [Fact]
    public void One_good_answer_clears_it_immediately()
    {
        var fail = true;
        using var world = new World(_ => fail
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("down") }
            : Json(PageWith(2)));

        for (var i = 0; i < LiveCompsLookup.FailuresBeforeDown; i++) world.Run($"antminer s19 {i}");
        Assert.False(world.Lookup.Health.Answering);

        fail = false;
        world.Run("antminer s19 recovered");

        var health = world.Lookup.Health;
        Assert.True(health.Answering);
        Assert.Equal(0, health.ConsecutiveFailures);
        Assert.Equal("", health.Note);
        Assert.NotNull(health.LastSuccessAt);
    }

    [Fact]
    public void No_recent_sales_is_an_answer_and_never_counts_against_the_source()
    {
        using var world = new World(_ => Json("{\"data\":{\"total_results\":0,\"products\":[]}}"));

        for (var i = 0; i < LiveCompsLookup.FailuresBeforeDown + 2; i++) world.Run($"a model nobody sells {i}");

        // eBay saying it has no recent sales for a thing is exactly the fact the call was spent to
        // learn. Counting it as a failure would write off a working source on a quiet market.
        Assert.True(world.Lookup.Health.Answering);
        Assert.True(world.Lookup.ShouldAttempt);
    }

    [Fact]
    public void A_deployment_with_no_key_says_that_instead()
    {
        using var world = new World { Enabled = false };

        var health = world.Lookup.Health;
        Assert.False(health.Configured);
        Assert.Contains("aren't set up here", health.Note);
    }

    // ── Scaffolding ──────────────────────────────────────────────────────────────────────────

    /// <summary>One app's worth of live lookups: a stub API, a scratch comps table, a movable clock.</summary>
    private sealed class World : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), $"live_comps_{Guid.NewGuid():N}");
        private LiveCompsLookup? _lookup;

        public World(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
        {
            Directory.CreateDirectory(_folder);
            Handler = new StubHandler(respond ?? (_ => Json(PageWith(2))));
            Usage   = new LiveCompsUsageStore(Path.Combine(_folder, "app.db"));
            Store   = new LiveCompsStore(new ExternalMarketplaceDb(Path.Combine(_folder, "comps.db")), new ActionLog());
        }

        public StubHandler Handler { get; }
        public LiveCompsUsageStore Usage { get; }
        public LiveCompsStore Store { get; }

        public DateTimeOffset Now { get; set; } = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        public int DailyLimit { get; init; } = 10;
        public bool Enabled { get; init; } = true;
        public string ApiKey { get; init; } = "a-scratch-key";

        /// <summary>Built once and then held, because the run map and the "one at a time" lock are its own.</summary>
        public LiveCompsLookup Lookup => _lookup ??= new LiveCompsLookup(
            new OpenWebNinjaClient(new StubFactory(Handler),
                new CredentialsStore(new StubCredentials(ApiKey)), new ActionLog()),
            Store,
            new LiveCompsBudget(Usage, UserScope.Desktop, DailyLimit, Enabled, () => Now),
            new ActionLog());

        /// <summary>Starts a lookup and waits for it, the way the browser's poll loop does.</summary>
        public LiveCompsRun Run(string query)
        {
            var run = Lookup.Start(query);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!run.Finished && DateTime.UtcNow < deadline) Thread.Sleep(15);
            Assert.True(run.Finished, $"the lookup for \"{query}\" never finished");
            return run;
        }

        public void Dispose()
        {
            Handler.Dispose();
            // Releases the file handles the pool is still holding, so the folder can actually go.
            // Process-wide, which is why this class is in the pooled-sqlite collection.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_folder, recursive: true); } catch { /* a held file is not a failure */ }
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _calls;

        /// <summary>How many calls were spent. The number every budget test is really about.</summary>
        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.True(request.Headers.Contains("X-API-Key"),
                "the key must travel in a header — a URL with a key in it ends up in logs");
            Assert.Contains("show_only=sold_items", request.RequestUri!.Query, StringComparison.Ordinal);

            Interlocked.Increment(ref _calls);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubCredentials(string apiKey) : ICredentialsSource
    {
        private readonly Credentials _data = new() { OpenWebNinjaApiKey = apiKey };
        public Credentials Read() => _data;
        public void Write(Credentials data) { }
        public bool IsProtectingUnreadableData => false;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    /// <summary>A response body shaped exactly like the real API's, verified against it 2026-08-14.</summary>
    private static string PageWith(int count)
    {
        var products = Enumerable.Range(1, count).Select(i => $$"""
            {
              "item_id": "10000000000{{i}}",
              "title": "Bitmain Antminer S19j Pro 104TH Bitcoin Miner",
              "url": "https://www.ebay.com/itm/10000000000{{i}}",
              "price": 139.99,
              "price_raw": "$139.99",
              "currency": "USD",
              "caption": "Sold Aug 13, 2026",
              "image": "https://i.ebayimg.com/images/g/x/s-l300.webp",
              "image_high_res": "https://i.ebayimg.com/images/g/x/s-l1600.webp",
              "condition": "Pre-Owned",
              "shipping": "+$49.99 delivery",
              "location": "United States",
              "seller_name": "a-seller",
              "seller_feedback_count": 412,
              "buying_format": "buy_it_now",
              "position": {{i}}
            }
            """);

        return $$$"""
            {"status":"OK","data":{"total_results":512,"current_page":1,"results_per_page":25,
             "products":[{{{string.Join(",", products)}}}]}}
            """;
    }

    private static JsonElement Item(string json) => JsonDocument.Parse(json).RootElement;

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static string ReadSource(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", name));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
