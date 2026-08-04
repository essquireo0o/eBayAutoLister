using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The Deal Radar's three testable halves: when it is allowed to look
/// (<see cref="DealRadarClock"/>), what it is allowed to interrupt someone for
/// (<see cref="DealRadarMatcher"/>), and what it remembers (<see cref="DealRadarStore"/>).
///
/// The expensive failures this locks down are all quiet ones: a schedule that turns into a polling
/// loop against somebody else's site, an alert quoting a profit off one loose comp, and — the one
/// that would get notifications switched off within a day — the same craigslist post announced 112
/// times over the fortnight it sits there.
/// </summary>
public class DealRadarClockTests
{
    private static DealWatch Watch(long id = 1, int interval = 180, bool enabled = true) => new()
    {
        Id = id, Name = "S19s", Query = "s19", ZipCode = "89101", IntervalMinutes = interval, Enabled = enabled,
    };

    private static readonly DateTimeOffset Noon = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, DealRadarClock.DefaultIntervalMinutes)]
    [InlineData(0, DealRadarClock.MinIntervalMinutes)]
    [InlineData(1, DealRadarClock.MinIntervalMinutes)]
    [InlineData(29, DealRadarClock.MinIntervalMinutes)]
    [InlineData(30, 30)]
    [InlineData(180, 180)]
    [InlineData(99999, DealRadarClock.MaxIntervalMinutes)]
    public void An_interval_can_never_be_set_below_the_floor(int? requested, int expected) =>
        Assert.Equal(expected, DealRadarClock.SanitizeInterval(requested));

    [Fact]
    public void The_next_run_is_the_interval_plus_a_stable_per_watch_offset()
    {
        // Deterministic in the id: the same watch always lands the same distance off the hour, so
        // restarting the app doesn't reshuffle the schedule.
        var watch = Watch(id: 3, interval: 180);
        var first = DealRadarClock.NextRun(watch, Noon);
        var again = DealRadarClock.NextRun(watch, Noon);

        Assert.Equal(first, again);
        Assert.Equal(Noon.AddMinutes(180 + DealRadarClock.JitterFor(3)), first);
    }

    [Fact]
    public void Two_watches_on_the_same_interval_do_not_march_in_lockstep()
    {
        var a = DealRadarClock.NextRun(Watch(id: 1), Noon);
        var b = DealRadarClock.NextRun(Watch(id: 2), Noon);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void A_watch_that_has_never_run_is_due_immediately()
    {
        var due = DealRadarClock.NextDueWatch([Watch()], Noon, lastScanUtc: null);
        Assert.NotNull(due);
    }

    [Fact]
    public void A_paused_watch_is_never_due()
    {
        var due = DealRadarClock.NextDueWatch([Watch(enabled: false)], Noon, lastScanUtc: null);
        Assert.Null(due);
    }

    [Fact]
    public void Only_one_watch_can_be_due_and_it_is_the_most_overdue_one()
    {
        var early = Watch(id: 1);
        early.NextRunUtc = Noon.AddHours(-3);
        var late = Watch(id: 2);
        late.NextRunUtc = Noon.AddMinutes(-5);

        var due = DealRadarClock.NextDueWatch([late, early], Noon, lastScanUtc: null);
        Assert.Equal(1, due!.Id);
    }

    [Fact]
    public void A_watch_whose_slot_has_not_come_round_is_not_due()
    {
        var watch = Watch();
        watch.NextRunUtc = Noon.AddMinutes(30);
        Assert.Null(DealRadarClock.NextDueWatch([watch], Noon, lastScanUtc: null));
    }

    [Fact]
    public void Nothing_scans_inside_the_global_gap_however_overdue_it_is()
    {
        // The restart case: after a day closed, every watch is overdue at once. Without this floor
        // that is twelve scans in twelve seconds at one site.
        var watch = Watch();
        watch.NextRunUtc = Noon.AddDays(-1);

        Assert.Null(DealRadarClock.NextDueWatch([watch], Noon, Noon.AddMinutes(-1)));
        Assert.NotNull(DealRadarClock.NextDueWatch([watch], Noon, Noon.AddMinutes(-DealRadarClock.MinGapMinutes - 1)));
    }

    [Fact]
    public void The_next_sweep_never_reads_as_a_time_in_the_past()
    {
        var watch = Watch();
        watch.NextRunUtc = Noon.AddHours(-4);
        Assert.Equal(Noon, DealRadarClock.NextScanDue([watch], Noon));
    }

    [Fact]
    public void With_nothing_enabled_there_is_no_next_sweep_to_report()
    {
        Assert.Null(DealRadarClock.NextScanDue([Watch(enabled: false)], Noon));
    }

    // ── Quiet hours ──────────────────────────────────────────────────────────

    private static DealRadarSettings Quiet(int from, int to, bool on = true) =>
        new() { QuietHoursEnabled = on, QuietFromHour = from, QuietToHour = to };

    [Theory]
    [InlineData(23, 7, 23, true)]   // the window a seller actually sets, and it wraps midnight
    [InlineData(23, 7, 2, true)]
    [InlineData(23, 7, 6, true)]
    [InlineData(23, 7, 7, false)]   // exclusive at the far end: 7am is when it stops
    [InlineData(23, 7, 12, false)]
    [InlineData(23, 7, 22, false)]
    [InlineData(1, 6, 3, true)]     // a same-day window still works
    [InlineData(1, 6, 8, false)]
    public void Quiet_hours_handle_the_overnight_wrap(int from, int to, int hour, bool expected)
    {
        var localNow = new DateTimeOffset(2026, 7, 27, hour, 30, 0, TimeSpan.Zero);
        Assert.Equal(expected, DealRadarClock.IsQuiet(Quiet(from, to), localNow));
    }

    [Fact]
    public void A_zero_width_window_means_never_quiet_not_always_quiet()
    {
        var localNow = new DateTimeOffset(2026, 7, 27, 3, 0, 0, TimeSpan.Zero);
        Assert.False(DealRadarClock.IsQuiet(Quiet(7, 7), localNow));
    }

    [Fact]
    public void Quiet_hours_switched_off_are_off_whatever_the_hours_say()
    {
        var localNow = new DateTimeOffset(2026, 7, 27, 2, 0, 0, TimeSpan.Zero);
        Assert.False(DealRadarClock.IsQuiet(Quiet(23, 7, on: false), localNow));
    }

    [Fact]
    public void The_quiet_window_is_described_in_the_clock_a_person_sets_it_in()
    {
        Assert.Equal("11pm — 7am", DealRadarClock.DescribeQuietHours(Quiet(23, 7)));
        Assert.Equal("12am — 12pm", DealRadarClock.DescribeQuietHours(Quiet(0, 12)));
        Assert.Equal("", DealRadarClock.DescribeQuietHours(Quiet(23, 7, on: false)));
    }
}

public class DealRadarMatcherTests
{
    private static readonly DateTimeOffset Found = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    private static DealWatch Watch(decimal minProfit = 75m, decimal minRoi = 40m) => new()
    {
        Id = 1, Name = "S19s under $500", Query = "s19", ZipCode = "89101",
        MinNetProfit = minProfit, MinRoiPercent = minRoi, RequireConfidentEvidence = true,
    };

    // A row exactly as LocalArbitrageAnalyzer.Build would leave it for a deal worth driving to.
    private static LocalArbitrageOpportunity Row(
        string id = "7712345678", decimal ask = 400m, decimal profit = 210m, decimal roi = 52m,
        string verdict = "goldmine", string evidence = LocalArbitrageEvidence.Confident) => new()
    {
        Source = "craigslist", SourceLabel = "Craigslist", ItemId = id,
        Title = "Antminer S19 95TH miner, works great",
        Url = $"https://lasvegas.craigslist.org/d/{id}.html",
        LocalAsk = ask, DistanceMiles = 3, Location = "Henderson",
        EbayExpectedSale = 700m, EbayResaleMedian = 690m,
        NetProfit = profit, RoiPercent = roi, MarginPercent = 30m, MaxBuyPrice = 610m,
        PricedCompCount = 11, Verdict = verdict, EvidenceTier = evidence, DaysToCash = 21,
    };

    // ── What is allowed to fire ──────────────────────────────────────────────

    [Fact]
    public void A_confident_profitable_row_over_the_bar_fires()
    {
        var alerts = DealRadarMatcher.Match(Watch(), Scan(Row()), Found);
        Assert.Single(alerts);
        Assert.Equal("craigslist:7712345678", alerts[0].ItemKey);
    }

    [Fact]
    public void A_row_the_app_refused_to_value_never_fires_at_any_threshold()
    {
        // The truck priced against tow-hitch comps. The board can show it with dashes and a search
        // link; a notification has no room for either, so it stays quiet.
        var row = Row();
        row.Valuation = new ResaleValuation { Status = ValuationStatuses.Manual };

        Assert.Empty(DealRadarMatcher.Match(Watch(minProfit: 0m, minRoi: 0m), Scan(row), Found));
    }

    [Fact]
    public void A_row_with_no_resale_price_never_fires()
    {
        var row = Row();
        row.EbayExpectedSale = null;
        row.EbayResaleMedian = null;

        Assert.Empty(DealRadarMatcher.Match(Watch(minProfit: 0m, minRoi: 0m), Scan(row), Found));
    }

    [Theory]
    [InlineData("goldmine", true)]
    [InlineData("solid", true)]
    [InlineData("thin", false)]
    [InlineData("pass", false)]
    [InlineData("no_data", false)]
    public void Only_verdicts_the_board_stands_behind_are_worth_waking_someone_for(string verdict, bool fires)
    {
        var alerts = DealRadarMatcher.Match(Watch(), Scan(Row(verdict: verdict)), Found);
        Assert.Equal(fires, alerts.Count == 1);
    }

    [Fact]
    public void Thin_evidence_is_refused_by_default()
    {
        Assert.Empty(DealRadarMatcher.Match(
            Watch(), Scan(Row(evidence: LocalArbitrageEvidence.Low)), Found));
    }

    [Fact]
    public void Thin_evidence_fires_only_when_the_seller_turned_the_gate_off()
    {
        var watch = Watch();
        watch.RequireConfidentEvidence = false;

        Assert.Single(DealRadarMatcher.Match(watch, Scan(Row(evidence: LocalArbitrageEvidence.Low)), Found));
    }

    [Fact]
    public void No_evidence_at_all_never_fires_even_with_the_gate_off()
    {
        // "none" is not thinner evidence, it is a price for a different product.
        var watch = Watch();
        watch.RequireConfidentEvidence = false;

        Assert.Empty(DealRadarMatcher.Match(watch, Scan(Row(evidence: LocalArbitrageEvidence.None)), Found));
    }

    [Fact]
    public void The_profit_floor_is_the_sellers_own_number()
    {
        Assert.Empty(DealRadarMatcher.Match(Watch(minProfit: 300m), Scan(Row(profit: 210m)), Found));
        Assert.Single(DealRadarMatcher.Match(Watch(minProfit: 210m), Scan(Row(profit: 210m)), Found));
    }

    [Fact]
    public void The_roi_floor_applies_independently_of_the_profit_floor()
    {
        Assert.Empty(DealRadarMatcher.Match(Watch(minProfit: 50m, minRoi: 90m), Scan(Row(roi: 52m)), Found));
    }

    [Fact]
    public void The_cash_ceiling_is_measured_on_what_actually_leaves_the_wallet()
    {
        // A retail row's till price includes sales tax. A ceiling read off the shelf price would
        // clear a deal the seller can't afford, by exactly the tax.
        var row = Row(ask: 480m);
        row.IsRetail = true;
        row.BuyCostAllIn = 516m;

        var watch = Watch();
        watch.MaxAsk = 500m;

        Assert.Empty(DealRadarMatcher.Match(watch, Scan(row), Found));
    }

    [Fact]
    public void A_row_further_away_than_the_seller_will_drive_does_not_fire()
    {
        var row = Row();
        row.DistanceMiles = 88;

        var watch = Watch();
        watch.MaxDistanceMiles = 25;

        Assert.Empty(DealRadarMatcher.Match(watch, Scan(row), Found));
    }

    [Fact]
    public void An_unstated_distance_passes_the_distance_filter()
    {
        // The radius already bounded the search. Dropping every classified that didn't publish a
        // mileage would empty the feature.
        var row = Row();
        row.DistanceMiles = null;

        var watch = Watch();
        watch.MaxDistanceMiles = 25;

        Assert.Single(DealRadarMatcher.Match(watch, Scan(row), Found));
    }

    // ── Once per listing, ever ───────────────────────────────────────────────

    [Fact]
    public void A_listing_already_alerted_on_is_not_alerted_on_again()
    {
        var seen = new HashSet<string> { "craigslist:7712345678" };
        Assert.Empty(DealRadarMatcher.Match(Watch(), Scan(Row()), Found, seen));
    }

    [Fact]
    public void The_same_post_found_twice_in_one_scan_is_one_alert()
    {
        var scan = Scan(Row(), Row());
        Assert.Single(DealRadarMatcher.Match(Watch(), scan, Found));
    }

    [Fact]
    public void An_item_key_is_scoped_to_its_site()
    {
        // Ids are only unique within a site: craigslist's 7712345678 and a feed's are not the same
        // thing, and merging them would silently suppress one site's find.
        var craigslist = DealRadarMatcher.ItemKey(Row());
        var facebook = Row();
        facebook.Source = "facebook";

        Assert.NotEqual(craigslist, DealRadarMatcher.ItemKey(facebook));
    }

    [Fact]
    public void A_listing_with_no_id_still_gets_a_stable_key_off_its_url()
    {
        var row = Row();
        row.ItemId = "";
        Assert.Equal($"craigslist:{row.Url}", DealRadarMatcher.ItemKey(row));
    }

    [Fact]
    public void The_biggest_money_is_reported_first()
    {
        var scan = Scan(Row(id: "1", profit: 120m), Row(id: "2", profit: 480m), Row(id: "3", profit: 260m));
        var alerts = DealRadarMatcher.Match(Watch(), scan, Found);

        Assert.Equal(["2", "3", "1"], alerts.Select(a => a.ItemKey.Split(':')[1]));
    }

    // ── The sentence ─────────────────────────────────────────────────────────

    [Fact]
    public void The_headline_says_the_buy_the_drive_the_resale_and_the_money()
    {
        var headline = DealRadarMatcher.Match(Watch(), Scan(Row()), Found)[0].Headline;

        Assert.Contains("400", headline);
        Assert.Contains("Antminer S19", headline);
        Assert.Contains("3 mi away", headline);
        Assert.Contains("700", headline);
        Assert.Contains("210", headline);
        Assert.Contains("30% margin", headline);
    }

    [Fact]
    public void An_unknown_distance_is_absent_from_the_headline_never_faked_as_zero()
    {
        var row = Row();
        row.DistanceMiles = null;

        var headline = DealRadarMatcher.Match(Watch(), Scan(row), Found)[0].Headline;

        Assert.DoesNotContain("0 mi", headline);
        Assert.Contains("Henderson", headline);   // the place it did state
    }

    [Fact]
    public void A_long_title_is_cut_rather_than_allowed_to_fill_the_notification()
    {
        var row = Row();
        row.Title = "Bitmain Antminer S19 Pro 110TH miner with PSU, power cord, tested, ready to hash today";

        var headline = DealRadarMatcher.Match(Watch(), Scan(row), Found)[0].Headline;
        Assert.Contains("…", headline);
        Assert.True(headline.Length < 130, $"headline is {headline.Length} chars: {headline}");
    }

    [Fact]
    public void Several_finds_in_one_run_are_summarised_rather_than_listed()
    {
        var alerts = DealRadarMatcher.Match(
            Watch(), Scan(Row(id: "1", profit: 120m), Row(id: "2", profit: 480m)), Found);

        var summary = DealRadarMatcher.SummaryHeadline(alerts);
        Assert.StartsWith("2 new deals worth", summary);
        Assert.Contains("600", summary);
        Assert.Contains("best:", summary);
    }

    [Fact]
    public void One_find_summarises_to_its_own_headline()
    {
        var alerts = DealRadarMatcher.Match(Watch(), Scan(Row()), Found);
        Assert.Equal(alerts[0].Headline, DealRadarMatcher.SummaryHeadline(alerts));
    }

    [Fact]
    public void The_notification_title_is_the_watch_the_seller_named()
    {
        Assert.Equal("S19s under $500", DealRadarMatcher.NotificationTitle(Watch(), 1));
        Assert.Equal("S19s under $500 · 3 new deals", DealRadarMatcher.NotificationTitle(Watch(), 3));
    }

    [Fact]
    public void An_alert_carries_the_boards_own_figures_untouched()
    {
        var alert = DealRadarMatcher.Match(Watch(), Scan(Row()), Found)[0];

        Assert.Equal(400m, alert.LocalAsk);
        Assert.Equal(700m, alert.ResalePrice);
        Assert.Equal(210m, alert.NetProfit);
        Assert.Equal(52m, alert.RoiPercent);
        Assert.Equal(610m, alert.MaxBuyPrice);
        Assert.Equal(11, alert.CompCount);
        Assert.Equal(LocalArbitrageEvidence.Confident, alert.EvidenceTier);
        Assert.Equal(21, alert.DaysToCash);
    }

    private static LocalArbitrageResult Scan(params LocalArbitrageOpportunity[] rows) => new()
    {
        Status = "ok", Query = "s19", ZipCode = "89101", RadiusMiles = 40,
        Items = [.. rows], LocalListingsFound = rows.Length,
    };
}

[Collection(PooledSqliteTests.Name)]
public class DealRadarStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"radar_{Guid.NewGuid():N}.db");

    private DealRadarStore NewStore() => new(_dbPath);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    private static DealWatchRequest Request() => new()
    {
        Query = "antminer s19", ZipCode = "89101", RadiusMiles = 40,
        Sources = "craigslist", MinNetProfit = 120m, MinRoiPercent = 45m, IntervalMinutes = 180,
    };

    [Fact]
    public void The_radar_ships_switched_off()
    {
        // Reading other people's sites on a timer is not something that starts happening because
        // the app was installed.
        Assert.False(NewStore().GetSettings().Enabled);
    }

    [Fact]
    public void A_watch_saves_and_reads_back()
    {
        var store = NewStore();
        var saved = store.SaveWatch(Request());

        var read = store.GetWatch(saved.Id)!;
        Assert.Equal("antminer s19", read.Query);
        Assert.Equal(120m, read.MinNetProfit);
        Assert.Equal(45m, read.MinRoiPercent);
        Assert.Equal(180, read.IntervalMinutes);
        Assert.True(read.Enabled);
    }

    [Fact]
    public void An_unnamed_watch_is_named_after_what_it_searches_for()
    {
        var saved = NewStore().SaveWatch(Request());
        Assert.Equal("antminer s19 · 40 mi of 89101", saved.Name);
    }

    [Fact]
    public void A_partial_edit_leaves_the_fields_it_did_not_name_alone()
    {
        // The pause toggle posts two fields. A whole-object bind would blank the profit bar with it.
        var store = NewStore();
        var saved = store.SaveWatch(Request());

        store.SaveWatch(new DealWatchRequest { Id = saved.Id, Enabled = false });

        var read = store.GetWatch(saved.Id)!;
        Assert.False(read.Enabled);
        Assert.Equal(120m, read.MinNetProfit);
        Assert.Equal("antminer s19", read.Query);
        Assert.Equal("craigslist", read.Sources);
    }

    [Fact]
    public void An_interval_below_the_floor_is_raised_to_it_on_the_way_in()
    {
        var saved = NewStore().SaveWatch(new DealWatchRequest
        {
            Query = "s19", ZipCode = "89101", IntervalMinutes = 1,
        });
        Assert.Equal(DealRadarClock.MinIntervalMinutes, saved.IntervalMinutes);
    }

    [Fact]
    public void A_watch_with_nothing_to_look_for_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            NewStore().SaveWatch(new DealWatchRequest { Query = "", ZipCode = "89101" }));
        Assert.Contains("something to look for", ex.Message);
    }

    [Fact]
    public void A_blank_keyword_is_a_real_search_on_a_category_board()
    {
        // "Everything on the cars board within 40 miles" is exactly what picking a category means.
        var saved = NewStore().SaveWatch(new DealWatchRequest
        {
            Query = "", CategoryId = ResaleCategoryCatalog.CarsId, ZipCode = "89101",
        });
        Assert.Equal(ResaleCategoryCatalog.CarsId, saved.CategoryId);
    }

    [Fact]
    public void A_watch_with_nowhere_to_look_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            NewStore().SaveWatch(new DealWatchRequest { Query = "s19", ZipCode = "" }));
        Assert.Contains("ZIP code", ex.Message);
    }

    [Fact]
    public void A_named_craigslist_board_stands_in_for_a_zip()
    {
        var saved = NewStore().SaveWatch(new DealWatchRequest
        {
            Query = "s19", ZipCode = "", CraigslistSite = "lasvegas",
        });
        Assert.Equal("lasvegas", saved.CraigslistSite);
    }

    [Fact]
    public void There_is_a_ceiling_on_how_many_sites_one_seller_can_have_read_for_them()
    {
        var store = NewStore();
        for (var i = 0; i < DealRadarClock.MaxWatches; i++)
            store.SaveWatch(new DealWatchRequest { Query = $"item {i}", ZipCode = "89101" });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            store.SaveWatch(new DealWatchRequest { Query = "one too many", ZipCode = "89101" }));
        Assert.Contains("at once", ex.Message);

        // The ceiling is on watches, not on edits — an existing one still saves.
        var existing = store.ListWatches()[0];
        store.SaveWatch(new DealWatchRequest { Id = existing.Id, MinNetProfit = 500m });
        Assert.Equal(500m, store.GetWatch(existing.Id)!.MinNetProfit);
    }

    [Fact]
    public void Editing_a_watch_that_was_deleted_in_another_tab_is_refused_rather_than_recreating_it()
    {
        var store = NewStore();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            store.SaveWatch(new DealWatchRequest { Id = 4242, MinNetProfit = 10m }));
        Assert.Contains("no longer exists", ex.Message);
    }

    [Fact]
    public void Recording_a_run_moves_the_watch_forward_and_keeps_its_running_totals()
    {
        var store = NewStore();
        var saved = store.SaveWatch(Request());
        var ranAt = new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

        store.RecordRun(saved.Id, RadarRunStatuses.Ok, "2 new deals cleared your bar.", 63, 2, 430m, ranAt);
        store.RecordRun(saved.Id, RadarRunStatuses.NoMatches, "Nothing new.", 58, 0, 0m, ranAt.AddHours(3));

        var read = store.GetWatch(saved.Id)!;
        Assert.Equal(RadarRunStatuses.NoMatches, read.LastStatus);
        Assert.Equal(58, read.LastScannedCount);
        Assert.Equal(0, read.LastMatchCount);
        Assert.Equal(2, read.TotalAlertCount);        // the running total survives an empty run
        Assert.Equal(430m, read.TotalProfitFound);
        Assert.True(read.NextRunUtc > ranAt.AddHours(3));
    }

    // ── The memory ───────────────────────────────────────────────────────────

    private static DealAlert Alert(long watchId, string key, decimal profit = 210m) => new()
    {
        WatchId = watchId, WatchName = "S19s", ItemKey = key,
        Title = "Antminer S19", Url = "https://example.org/1", Source = "craigslist",
        SourceLabel = "Craigslist", LocalAsk = 400m, ResalePrice = 700m, NetProfit = profit,
        RoiPercent = 52m, MarginPercent = 30m, MaxBuyPrice = 610m, CompCount = 11,
        EvidenceTier = LocalArbitrageEvidence.Confident, Verdict = "goldmine",
        Headline = "$400 Antminer S19", FoundUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void An_alert_saves_with_every_figure_the_board_gave_it()
    {
        var store = NewStore();
        var watch = store.SaveWatch(Request());
        store.AddAlerts([Alert(watch.Id, "craigslist:1")]);

        var read = store.ListAlerts()[0];
        Assert.Equal(400m, read.LocalAsk);
        Assert.Equal(700m, read.ResalePrice);
        Assert.Equal(210m, read.NetProfit);
        Assert.Equal(610m, read.MaxBuyPrice);
        Assert.Equal(11, read.CompCount);
        Assert.False(read.Read);
    }

    [Fact]
    public void The_same_listing_offered_twice_is_stored_once()
    {
        var store = NewStore();
        var watch = store.SaveWatch(Request());

        Assert.Single(store.AddAlerts([Alert(watch.Id, "craigslist:1")]));
        Assert.Empty(store.AddAlerts([Alert(watch.Id, "craigslist:1")]));
        Assert.Single(store.ListAlerts());
    }

    [Fact]
    public void Two_watches_can_each_find_the_same_listing()
    {
        // They are different searches with different bars; suppressing the second would hide a find
        // from a watch that never reported it.
        var store = NewStore();
        var a = store.SaveWatch(Request());
        var b = store.SaveWatch(new DealWatchRequest { Query = "miner", ZipCode = "89101" });

        Assert.Single(store.AddAlerts([Alert(a.Id, "craigslist:1")]));
        Assert.Single(store.AddAlerts([Alert(b.Id, "craigslist:1")]));
    }

    [Fact]
    public void The_seen_keys_are_what_the_matcher_dedupes_against()
    {
        var store = NewStore();
        var watch = store.SaveWatch(Request());
        store.AddAlerts([Alert(watch.Id, "craigslist:1")]);

        Assert.Contains("craigslist:1", store.SeenKeys(watch.Id));
        Assert.Empty(store.SeenKeys(watch.Id + 99));
    }

    [Fact]
    public void Clearing_the_feed_does_not_make_old_listings_new_again()
    {
        // The whole reason the memory is a separate table: prune the list of finds and every
        // classified still up on the site would be pushed at the seller a second time.
        var store = NewStore();
        var watch = store.SaveWatch(Request());
        store.AddAlerts([Alert(watch.Id, "craigslist:1")]);

        store.ClearAlerts();

        Assert.Empty(store.ListAlerts());
        Assert.Contains("craigslist:1", store.SeenKeys(watch.Id));
        Assert.Empty(store.AddAlerts([Alert(watch.Id, "craigslist:1")]));
    }

    [Fact]
    public void Deleting_a_watch_takes_its_alerts_and_its_memory_with_it()
    {
        var store = NewStore();
        var watch = store.SaveWatch(Request());
        store.AddAlerts([Alert(watch.Id, "craigslist:1")]);

        Assert.True(store.DeleteWatch(watch.Id));
        Assert.Empty(store.ListAlerts());
        Assert.Empty(store.SeenKeys(watch.Id));
    }

    [Fact]
    public void Dismissing_takes_an_alert_off_the_feed_without_forgetting_it()
    {
        var store = NewStore();
        var watch = store.SaveWatch(Request());
        var stored = store.AddAlerts([Alert(watch.Id, "craigslist:1")])[0];

        store.SetAlertFlag(stored.Id, "dismissed", true);

        Assert.Empty(store.ListAlerts());
        Assert.Single(store.ListAlerts(includeDismissed: true));
        Assert.Contains("craigslist:1", store.SeenKeys(watch.Id));
    }

    [Fact]
    public void Only_the_three_known_flags_can_be_written()
    {
        var store = NewStore();
        var watch = store.SaveWatch(Request());
        var stored = store.AddAlerts([Alert(watch.Id, "craigslist:1")])[0];

        Assert.False(store.SetAlertFlag(stored.Id, "net_profit", true));
        Assert.False(store.SetAlertFlag(stored.Id, "1=1; DROP TABLE radar_alerts", true));
        Assert.Single(store.ListAlerts());
    }

    [Fact]
    public void The_counts_behind_the_badge_ignore_read_and_dismissed_alerts()
    {
        var store = NewStore();
        var watch = store.SaveWatch(Request());
        var stored = store.AddAlerts([
            Alert(watch.Id, "craigslist:1", 210m),
            Alert(watch.Id, "craigslist:2", 90m),
            Alert(watch.Id, "craigslist:3", 40m),
        ]);

        store.SetAlertFlag(stored[0].Id, "read", true);
        store.SetAlertFlag(stored[1].Id, "dismissed", true);

        var counts = store.AlertCounts();
        Assert.Equal(1, counts.Unread);
        Assert.Equal(2, counts.Total);          // dismissed is off the feed, read is not
        Assert.Equal(40m, counts.UnreadProfit);
    }

    [Fact]
    public void Marking_everything_read_clears_the_badge_without_clearing_the_feed()
    {
        var store = NewStore();
        var watch = store.SaveWatch(Request());
        store.AddAlerts([Alert(watch.Id, "craigslist:1"), Alert(watch.Id, "craigslist:2")]);

        Assert.Equal(2, store.MarkAllRead());
        Assert.Equal(0, store.AlertCounts().Unread);
        Assert.Equal(2, store.ListAlerts().Count);
    }

    [Fact]
    public void Pruning_drops_read_alerts_before_unread_ones()
    {
        var store = NewStore();
        var watch = store.SaveWatch(Request());

        var many = Enumerable.Range(0, DealRadarStore.MaxAlertsKept + 20)
            .Select(i => Alert(watch.Id, $"craigslist:{i}")).ToList();
        var stored = store.AddAlerts(many);
        // The oldest are read; those are the ones a prune should take.
        foreach (var alert in stored.Take(40)) store.SetAlertFlag(alert.Id, "read", true);

        store.Prune(DateTimeOffset.UtcNow);

        Assert.Equal(DealRadarStore.MaxAlertsKept, store.ListAlerts(DealRadarStore.MaxAlertsKept).Count);
        Assert.Equal(many.Count - 40, store.AlertCounts().Unread);
    }

    [Fact]
    public void Settings_round_trip_and_are_clamped_to_a_real_clock()
    {
        var store = NewStore();
        store.SaveSettings(new DealRadarSettings
        {
            Enabled = true, QuietHoursEnabled = true, QuietFromHour = 99, QuietToHour = -4,
            DesktopNotifications = false,
        });

        var read = store.GetSettings();
        Assert.True(read.Enabled);
        Assert.Equal(23, read.QuietFromHour);
        Assert.Equal(0, read.QuietToHour);
        Assert.False(read.DesktopNotifications);
    }
}

public class DealRadarPostureTests
{
    [Fact]
    public void A_watch_that_named_no_sites_reads_the_public_one()
    {
        // Never "everything available", which would quietly enrol a connected Facebook session into
        // an unattended schedule the seller never asked for.
        Assert.Equal(CraigslistParser.SourceId, DealRadarService.EffectiveSources(new DealWatch()));
        Assert.Equal(CraigslistParser.SourceId, DealRadarService.EffectiveSources(new DealWatch { Sources = "   " }));
    }

    [Fact]
    public void A_watch_that_named_its_sites_gets_exactly_those()
    {
        Assert.Equal("craigslist,facebook",
            DealRadarService.EffectiveSources(new DealWatch { Sources = " craigslist,facebook " }));
    }

    [Fact]
    public void A_notifier_with_no_tray_attached_says_so_rather_than_promising_a_balloon()
    {
        var notifier = new DesktopNotifier();
        Assert.Equal(RadarChannels.Browser, notifier.Channel);

        notifier.AttachDesktopChannel();
        Assert.Equal(RadarChannels.Tray, notifier.Channel);

        notifier.DetachDesktopChannel();
        Assert.Equal(RadarChannels.Browser, notifier.Channel);
    }

    [Fact]
    public void A_notification_with_nowhere_to_go_is_recorded_and_reported_as_undelivered()
    {
        var notifier = new DesktopNotifier();
        Assert.False(notifier.Send(new DesktopNotification("Deal Radar", "$400 S19", 1)));
        Assert.Single(notifier.Recent);
    }

    [Fact]
    public void A_channel_that_throws_never_takes_the_scan_down_with_it()
    {
        var notifier = new DesktopNotifier();
        notifier.Notified += _ => throw new InvalidOperationException("the tray was disposed");

        Assert.False(notifier.Send(new DesktopNotification("Deal Radar", "$400 S19", 1)));
    }

    [Fact]
    public void A_delivered_notification_reports_delivered()
    {
        var notifier = new DesktopNotifier();
        DesktopNotification? seen = null;
        notifier.Notified += n => seen = n;

        Assert.True(notifier.Send(new DesktopNotification("Deal Radar", "$400 S19", 7)));
        Assert.Equal(7, seen!.AlertId);
    }
}
