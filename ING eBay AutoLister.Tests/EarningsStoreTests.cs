using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The one hard requirement on this table: pressing Import twice must not double the seller's
// earnings. The natural way to use that button is to press it again, so an import that appends
// instead of updating would inflate the headline every single time.
[Collection(PooledSqliteTests.Name)]
public class EarningsStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"earnings_{Guid.NewGuid():N}.db");

    private EarningsStore NewStore() => new(_dbPath);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    private static FlipRecord EbayLine(decimal price = 1000m, string line = "LI-1") => new()
    {
        Source = "ebay",
        OrderId = "ORD-1",
        LineItemId = line,
        ListingId = "110001",
        Title = "Antminer S19",
        SoldUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        SalePrice = price,
        MarketplaceFee = 132.90m,
    };

    [Fact]
    public void Saves_and_reads_back_a_sale()
    {
        var store = NewStore();
        store.Upsert(EbayLine());

        var all = store.GetAll();

        Assert.Single(all);
        Assert.Equal(1000m, all[0].SalePrice);
        Assert.Equal("110001", all[0].ListingId);
    }

    [Fact]
    public void Reimporting_the_same_order_line_updates_instead_of_doubling_the_money()
    {
        var store = NewStore();
        Assert.True(store.Upsert(EbayLine()));
        Assert.False(store.Upsert(EbayLine(price: 1050m)));

        var all = store.GetAll();

        Assert.Single(all);
        Assert.Equal(1050m, all[0].SalePrice);
    }

    [Fact]
    public void Different_lines_of_the_same_order_are_separate_sales()
    {
        var store = NewStore();
        store.Upsert(EbayLine(line: "LI-1"));
        store.Upsert(EbayLine(line: "LI-2"));

        Assert.Equal(2, store.GetAll().Count);
    }

    [Fact]
    public void Manual_flips_have_no_import_key_and_never_collapse_into_each_other()
    {
        var store = NewStore();
        store.Upsert(EarningsStore.FromRequest(new FlipUpsertRequest { Title = "Drill", SalePrice = 80m }));
        store.Upsert(EarningsStore.FromRequest(new FlipUpsertRequest { Title = "Drill", SalePrice = 80m }));

        // Two identical garage-sale drills really can both have sold.
        Assert.Equal(2, store.GetAll().Count);
    }

    [Fact]
    public void An_edit_changes_only_the_fields_that_were_supplied()
    {
        var store = NewStore();
        var flip = EbayLine();
        store.Upsert(flip);

        var updated = store.ApplyEdit(flip.Id, new FlipUpsertRequest { UnitCost = 400m });

        Assert.NotNull(updated);
        Assert.Equal(400m, updated.UnitCost);
        // The import key survives, so the next import still recognises this row rather than adding
        // a second copy of the same sale.
        Assert.Equal("ORD-1", updated.OrderId);
        Assert.Equal("LI-1", updated.LineItemId);
        Assert.Equal(1000m, updated.SalePrice);
    }

    [Fact]
    public void Editing_a_sale_that_is_gone_reports_it_rather_than_creating_one()
    {
        Assert.Null(NewStore().ApplyEdit(9999, new FlipUpsertRequest { UnitCost = 10m }));
    }

    [Fact]
    public void Deleting_removes_the_sale()
    {
        var store = NewStore();
        var flip = EbayLine();
        store.Upsert(flip);

        Assert.True(store.Delete(flip.Id));
        Assert.Empty(store.GetAll());
        Assert.False(store.Delete(flip.Id));
    }

    // ── The inputs that would quietly corrupt a running total ────────────────────────────────

    [Fact]
    public void A_sale_with_no_name_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            EarningsStore.FromRequest(new FlipUpsertRequest { SalePrice = 100m }));
    }

    [Fact]
    public void Negative_money_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            EarningsStore.FromRequest(new FlipUpsertRequest { Title = "Drill", SalePrice = -100m }));
        Assert.Throws<InvalidOperationException>(() =>
            EarningsStore.FromRequest(new FlipUpsertRequest { Title = "Drill", SalePrice = 100m, UnitCost = -1m }));
    }

    [Fact]
    public void A_refund_larger_than_what_the_buyer_paid_is_refused()
    {
        // It would read as negative revenue and drag the all-time total below what was really lost.
        Assert.Throws<InvalidOperationException>(() =>
            EarningsStore.FromRequest(new FlipUpsertRequest { Title = "Drill", SalePrice = 100m, RefundedAmount = 500m }));
    }

    [Fact]
    public void A_sale_dated_in_the_future_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            EarningsStore.FromRequest(new FlipUpsertRequest
            {
                Title = "Drill", SalePrice = 100m, SoldUtc = DateTimeOffset.UtcNow.AddYears(1),
            }));
    }

    [Fact]
    public void Status_is_normalised_so_a_typo_cannot_void_a_real_sale()
    {
        Assert.Equal("cancelled", EarningsStore.NormalizeStatus("CANCELED"));
        Assert.Equal("cancelled", EarningsStore.NormalizeStatus("cancelled"));
        Assert.Equal("refunded", EarningsStore.NormalizeStatus("Refunded"));
        Assert.Equal("paid", EarningsStore.NormalizeStatus("whatever"));
        Assert.Equal("paid", EarningsStore.NormalizeStatus(null));
    }

    [Fact]
    public void Quantity_below_one_is_lifted_rather_than_zeroing_the_cost_of_goods()
    {
        var flip = EarningsStore.FromRequest(new FlipUpsertRequest { Title = "Drill", SalePrice = 80m, Quantity = 0 });

        Assert.Equal(1, flip.Quantity);
    }
}
