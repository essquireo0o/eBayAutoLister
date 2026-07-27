using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The one hard requirement on this table: pressing "Track" twice on the same local post must not
// produce two cards. A duplicate deal duplicates its capital at risk and its projected profit into
// every total on the board, which is how a pipeline starts lying about how much money is out.
public class DealStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"deals_{Guid.NewGuid():N}.db");

    private DealStore NewStore() => new(_dbPath);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    private static DealUpsertRequest Tracked(decimal ask = 450m, string itemId = "cl-7712345678") => new()
    {
        Title = "Antminer S19 95TH",
        Source = "craigslist",
        SourceLabel = "Craigslist",
        SourceItemId = itemId,
        SourceUrl = "https://lasvegas.craigslist.org/view/d/antminer/7712345678.html",
        AskPrice = ask,
        MaxBuyPrice = 780m,
        ProjectedSalePrice = 1100m,
        ProjectedNetProfit = 320m,
        ProjectedRoiPercent = 71m,
        ProjectedDaysToCash = 24,
        ProjectedBasis = "14 sold comps · High confidence",
    };

    [Fact]
    public void Saves_and_reads_back_a_tracked_deal()
    {
        var store = NewStore();
        store.Upsert(DealStore.FromRequest(Tracked()));

        var all = store.GetAll();

        Assert.Single(all);
        Assert.Equal("Antminer S19 95TH", all[0].Title);
        Assert.Equal(DealStages.Sourced, all[0].Stage);
        Assert.Equal(450m, all[0].AskPrice);
        Assert.Equal(320m, all[0].ProjectedNetProfit);
        Assert.Equal("14 sold comps · High confidence", all[0].ProjectedBasis);
    }

    [Fact]
    public void Tracking_the_same_post_twice_updates_instead_of_duplicating_the_capital()
    {
        var store = NewStore();
        Assert.True(store.Upsert(DealStore.FromRequest(Tracked())));
        Assert.False(store.Upsert(DealStore.FromRequest(Tracked(ask: 400m))));

        var all = store.GetAll();

        Assert.Single(all);
        Assert.Equal(400m, all[0].AskPrice);
    }

    [Fact]
    public void Two_different_posts_are_two_deals()
    {
        var store = NewStore();
        store.Upsert(DealStore.FromRequest(Tracked(itemId: "cl-1")));
        store.Upsert(DealStore.FromRequest(Tracked(itemId: "cl-2")));

        Assert.Equal(2, store.GetAll().Count);
    }

    // Manual deals have no source key, so two of them are two deals even with identical text —
    // the alternative is refusing to let a seller track the second of a matched pair.
    [Fact]
    public void Hand_entered_deals_are_never_collapsed_together()
    {
        var store = NewStore();
        store.Upsert(DealStore.FromRequest(new DealUpsertRequest { Title = "Dell OptiPlex" }));
        store.Upsert(DealStore.FromRequest(new DealUpsertRequest { Title = "Dell OptiPlex" }));

        Assert.Equal(2, store.GetAll().Count);
    }

    [Fact]
    public void A_deal_needs_a_name()
    {
        var store = NewStore();
        Assert.Throws<InvalidOperationException>(() => DealStore.FromRequest(new DealUpsertRequest { Title = "  " }));
    }

    // Capital at risk is the number on this board that must never be understated, and a bought
    // deal with no price paid reports zero.
    [Fact]
    public void Moving_to_bought_without_a_price_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DealStore.FromRequest(new DealUpsertRequest { Title = "Dell OptiPlex", Stage = "bought" }));
    }

    [Fact]
    public void Negative_money_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DealStore.FromRequest(new DealUpsertRequest { Title = "Dell", Stage = "bought", PurchasePrice = -5m }));
        Assert.Throws<InvalidOperationException>(() =>
            DealStore.FromRequest(new DealUpsertRequest { Title = "Dell", Stage = "bought", PurchasePrice = 5m, PurchaseExtraCost = -1m }));
    }

    [Fact]
    public void A_purchase_date_in_the_future_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => DealStore.FromRequest(new DealUpsertRequest
        {
            Title = "Dell", Stage = "bought", PurchasePrice = 100m, BoughtUtc = DateTimeOffset.UtcNow.AddDays(30),
        }));
    }

    // A deal dragged straight from Sourced to Listed still has to be able to answer "how long has
    // this been sitting?", which needs a date for every stage it passed through.
    [Fact]
    public void Skipping_a_stage_still_stamps_the_stages_it_passed()
    {
        var store = NewStore();
        var deal = DealStore.FromRequest(new DealUpsertRequest
        {
            Title = "Dell OptiPlex", Stage = "listed", PurchasePrice = 60m, ListingId = "110009",
        });
        store.Upsert(deal);

        var saved = store.GetAll()[0];
        Assert.NotNull(saved.BoughtUtc);
        Assert.NotNull(saved.ListedUtc);
        Assert.Null(saved.SoldUtc);
    }

    [Fact]
    public void A_projection_is_dated_when_it_is_supplied()
    {
        var withForecast = DealStore.FromRequest(Tracked());
        var without = DealStore.FromRequest(new DealUpsertRequest { Title = "Dell OptiPlex" });

        Assert.NotNull(withForecast.ProjectedUtc);
        Assert.Null(without.ProjectedUtc);
    }

    // Field-level editing exists so the board can change one cell without the browser round-
    // tripping the frozen forecast — the one field on a deal that can't be reconstructed later.
    [Fact]
    public void An_edit_leaves_the_fields_it_did_not_mention_alone()
    {
        var store = NewStore();
        var deal = DealStore.FromRequest(Tracked());
        store.Upsert(deal);

        store.ApplyEdit(deal.Id, new DealUpsertRequest { Stage = "bought", PurchasePrice = 380m });

        var saved = store.Get(deal.Id)!;
        Assert.Equal(DealStages.Bought, saved.Stage);
        Assert.Equal(380m, saved.PurchasePrice);
        Assert.Equal(320m, saved.ProjectedNetProfit);
        Assert.Equal(450m, saved.AskPrice);
        Assert.Equal("14 sold comps · High confidence", saved.ProjectedBasis);
    }

    [Fact]
    public void Changing_the_stage_restarts_the_days_in_stage_clock()
    {
        var store = NewStore();
        var deal = DealStore.FromRequest(Tracked());
        deal.StageChangedUtc = DateTimeOffset.UtcNow.AddDays(-30);
        store.Upsert(deal);

        store.ApplyEdit(deal.Id, new DealUpsertRequest { Stage = "bought", PurchasePrice = 380m });

        Assert.True(store.Get(deal.Id)!.StageChangedUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    // Re-saving the same stage must NOT restart it, or a card could never age into a stall warning
    // as long as anything else about it was being edited.
    [Fact]
    public void Editing_without_changing_the_stage_leaves_the_clock_running()
    {
        var store = NewStore();
        var deal = DealStore.FromRequest(Tracked());
        var stamped = DateTimeOffset.UtcNow.AddDays(-30);
        deal.StageChangedUtc = stamped;
        store.Upsert(deal);

        store.ApplyEdit(deal.Id, new DealUpsertRequest { Stage = "sourced", Note = "seller replied" });

        Assert.Equal(stamped.UtcDateTime, store.Get(deal.Id)!.StageChangedUtc.UtcDateTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Deleting_a_deal_removes_it()
    {
        var store = NewStore();
        var deal = DealStore.FromRequest(Tracked());
        store.Upsert(deal);

        Assert.True(store.Delete(deal.Id));
        Assert.Empty(store.GetAll());
        Assert.False(store.Delete(deal.Id));
    }

    [Theory]
    [InlineData("BOUGHT", DealStages.Bought)]
    [InlineData("purchased", DealStages.Bought)]
    [InlineData("live", DealStages.Listed)]
    [InlineData("dead", DealStages.Dropped)]
    [InlineData("walked", DealStages.Dropped)]
    [InlineData("nonsense", DealStages.Sourced)]
    [InlineData(null, DealStages.Sourced)]
    public void Stage_names_normalise(string? input, string expected) =>
        Assert.Equal(expected, DealStages.Normalize(input));
}
