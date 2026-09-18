using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// A dropshipper's cost is a share of each sale, not a number of dollars. The owner's books are the
// regression: "keep 40%" was typed against a 3-unit $825 sale, saved as $495, and that $495 then
// priced every other sale of the listing — 42 of them, selling at $220 to $430 — as a loss of
// $110 to $290 each. A month that made money reported -$2,197.74.
[Collection(PooledSqliteTests.Name)]
public class DropshipKeepPercentTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"keep_pct_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    private static EarningsCalculator NewCalculator() => new(new ProfitCalculator());

    private static FlipRecord Sale(decimal price, int quantity = 1, decimal fee = 0m, decimal refunded = 0m, string status = "paid") => new()
    {
        Source = "ebay", Title = "Antminer S19k Pro 120TH", ListingId = "287193228423", SoldUtc = DateTimeOffset.UtcNow,
        Quantity = quantity, SalePrice = price, MarketplaceFee = fee, ShippingCost = 0m, UnitCost = null,
        RefundedAmount = refunded, Status = status,
    };

    [Fact]
    public void A_split_prices_each_sale_from_its_own_price()
    {
        var split = new CostBasisEntry { ListingId = "287193228423", KeepPercent = 40m };
        var calculator = NewCalculator();

        var dear = calculator.Compute(Sale(825m, quantity: 3), split, new FeeProfile());
        var cheap = calculator.Compute(Sale(245m), split, new FeeProfile());

        Assert.Equal(495m, dear.CostOfGoods);    // 60% of $825 — for all three units, not each
        Assert.Equal(330m, dear.NetProfit);
        Assert.Equal(147m, cheap.CostOfGoods);   // 60% of $245, not the $495 the other sale produced
        Assert.Equal(98m, cheap.NetProfit);
    }

    [Fact]
    public void A_split_is_taken_before_ebays_fee_and_the_fee_still_comes_off()
    {
        var profit = NewCalculator().Compute(Sale(275m, fee: 38.94m), new CostBasisEntry { KeepPercent = 40m }, new FeeProfile());

        Assert.Equal(165m, profit.CostOfGoods);
        Assert.Equal(71.06m, profit.NetProfit);
        Assert.Contains(profit.Caveats, c => c.Contains("keep 40%"));
    }

    [Fact]
    public void A_fully_refunded_sale_on_a_split_owes_the_supplier_nothing()
    {
        var profit = NewCalculator().Compute(Sale(259.99m, refunded: 259.99m), new CostBasisEntry { KeepPercent = 40m }, new FeeProfile());

        Assert.Equal(0m, profit.GrossRevenue);
        Assert.Equal(0m, profit.CostOfGoods);
        Assert.Equal(0m, profit.NetProfit);
    }

    [Fact]
    public void A_fixed_cost_still_ignores_the_sale_price()
    {
        var fixedCost = new CostBasisEntry { UnitCost = 85m, InboundShipping = 5m };

        Assert.Equal(90m, fixedCost.UnitCostAt(40m));
        Assert.Equal(90m, fixedCost.UnitCostAt(4000m));
        Assert.Equal(180m, NewCalculator().Compute(Sale(500m, quantity: 2), fixedCost, new FeeProfile()).CostOfGoods);
    }

    [Theory]
    [InlineData(100, 275, 0)]      // keeps everything: the goods were free
    [InlineData(0, 275, 275)]      // keeps nothing: the whole sale is the supplier's
    [InlineData(40, 33.79, 20.27)] // rounds to the cent
    public void The_supplier_share_is_whatever_the_seller_does_not_keep(decimal keep, decimal unitPrice, decimal expectedCost) =>
        Assert.Equal(expectedCost, new CostBasisEntry { KeepPercent = keep }.UnitCostAt(unitPrice));

    [Fact]
    public void The_split_survives_the_database_as_a_percentage()
    {
        var store = new CostBasisStore(_dbPath);
        store.Save(new CostBasisEntry { ListingId = "287193228423", KeepPercent = 40m });
        store.Save(new CostBasisEntry { ListingId = "278225404863", UnitCost = 85m });

        var reopened = new CostBasisStore(_dbPath);

        Assert.Equal(40m, reopened.Find("287193228423", null)!.KeepPercent);
        Assert.Null(reopened.Find("278225404863", null)!.KeepPercent);   // a dollar cost stays a dollar cost
    }

    [Fact]
    public void A_database_from_before_the_column_existed_opens_and_keeps_its_costs()
    {
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE listing_cost_basis (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, user_id INTEGER NOT NULL DEFAULT 0,
                    listing_id TEXT NOT NULL DEFAULT '', sku TEXT NOT NULL DEFAULT '',
                    unit_cost NUMERIC NOT NULL DEFAULT 0, inbound_shipping NUMERIC NOT NULL DEFAULT 0,
                    note TEXT NOT NULL DEFAULT '', acquired_at TEXT NOT NULL DEFAULT '', updated_at TEXT NOT NULL DEFAULT '');
                INSERT INTO listing_cost_basis (listing_id, unit_cost) VALUES ('1100', 420);
                """;
            create.ExecuteNonQuery();
        }

        var store = new CostBasisStore(_dbPath);

        var old = store.Find("1100", null);
        Assert.Equal(420m, old!.UnitCost);
        Assert.Null(old.KeepPercent);
        store.Save(new CostBasisEntry { ListingId = "1100", KeepPercent = 35m });
        Assert.Equal(35m, store.Find("1100", null)!.KeepPercent);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100.5)]
    public void A_split_outside_zero_to_a_hundred_is_refused(decimal keep) =>
        Assert.Throws<InvalidOperationException>(() =>
            new CostBasisStore(_dbPath).Save(new CostBasisEntry { ListingId = "1100", KeepPercent = keep }));
}
