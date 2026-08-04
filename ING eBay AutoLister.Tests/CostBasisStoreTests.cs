using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// Cost basis is the one number in the profit calculation that eBay cannot supply, so losing it
// silently — on a relist, or on a save that collides with an existing row — would quietly turn
// every break-even check back into a guess.
[Collection(PooledSqliteTests.Name)]
public class CostBasisStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cost_basis_{Guid.NewGuid():N}.db");

    private CostBasisStore NewStore() => new(_dbPath);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Saves_and_reads_back_a_cost_basis()
    {
        var store = NewStore();
        store.Save(new CostBasisEntry { ListingId = "1100", Sku = "SKU-1", UnitCost = 420m, InboundShipping = 35m });

        var found = store.Find("1100", null);

        Assert.NotNull(found);
        Assert.Equal(420m, found.UnitCost);
        Assert.Equal(455m, found.TotalUnitCost);   // inbound freight is part of what the item cost
    }

    [Fact]
    public void Saving_the_same_listing_twice_updates_rather_than_duplicates()
    {
        var store = NewStore();
        store.Save(new CostBasisEntry { ListingId = "1100", UnitCost = 400m });
        store.Save(new CostBasisEntry { ListingId = "1100", UnitCost = 450m });

        Assert.Single(store.GetAll());
        Assert.Equal(450m, store.Find("1100", null)!.UnitCost);
    }

    [Fact]
    public void A_relisted_item_keeps_its_cost_through_the_sku()
    {
        // eBay hands out a new listing ID on a relist; the SKU is what survives.
        var store = NewStore();
        store.Save(new CostBasisEntry { ListingId = "1100", Sku = "SKU-1", UnitCost = 420m });

        var afterRelist = store.Find("2200", "SKU-1");

        Assert.NotNull(afterRelist);
        Assert.Equal(420m, afterRelist.UnitCost);
    }

    [Fact]
    public void A_listing_id_match_wins_over_a_sku_match()
    {
        var entries = new List<CostBasisEntry>
        {
            new() { ListingId = "1100", Sku = "SKU-OTHER", UnitCost = 100m },
            new() { ListingId = "9900", Sku = "SKU-1",     UnitCost = 200m },
        };

        Assert.Equal(100m, CostBasisStore.Find(entries, "1100", "SKU-1")!.UnitCost);
    }

    [Fact]
    public void A_save_that_supplies_both_keys_collapses_the_rows_they_matched_separately()
    {
        var store = NewStore();
        store.Save(new CostBasisEntry { ListingId = "1100", UnitCost = 100m });
        store.Save(new CostBasisEntry { Sku = "SKU-1", UnitCost = 200m });

        store.Save(new CostBasisEntry { ListingId = "1100", Sku = "SKU-1", UnitCost = 300m });

        Assert.Single(store.GetAll());
        Assert.Equal(300m, store.Find("1100", "SKU-1")!.UnitCost);
    }

    [Fact]
    public void Nothing_is_found_for_a_listing_with_no_recorded_cost()
        => Assert.Null(NewStore().Find("does-not-exist", "nor-does-this"));

    [Fact]
    public void An_entry_with_neither_key_is_rejected()
        => Assert.Throws<InvalidOperationException>(() => NewStore().Save(new CostBasisEntry { UnitCost = 10m }));

    [Fact]
    public void A_negative_cost_is_rejected()
        => Assert.Throws<InvalidOperationException>(
            () => NewStore().Save(new CostBasisEntry { ListingId = "1100", UnitCost = -1m }));

    [Fact]
    public void Deleting_removes_the_entry()
    {
        var store = NewStore();
        store.Save(new CostBasisEntry { ListingId = "1100", UnitCost = 400m });

        Assert.True(store.Delete("1100", null));
        Assert.Null(store.Find("1100", null));
        Assert.False(store.Delete("1100", null));
    }

    [Fact]
    public void Entries_survive_a_new_store_over_the_same_database()
    {
        NewStore().Save(new CostBasisEntry { ListingId = "1100", UnitCost = 400m });

        Assert.Equal(400m, NewStore().Find("1100", null)!.UnitCost);
    }
}
