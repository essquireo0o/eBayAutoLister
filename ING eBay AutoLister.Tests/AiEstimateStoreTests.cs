using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The app remembers what the AI told it, so the same product is never paid for twice.
/// </summary>
/// <remarks>
/// The owner, 2026-08-21: "calculate products using scraper first then AI … you save everything
/// to the database". The order was already right — stored comps, then a live sold lookup, then
/// the AI for whatever those could not answer. What was missing was the memory: every scan
/// re-asked the model about the same forty listings, a generation spent and fifteen seconds
/// waited to be told again what a Razor moped sells for. Measured after this: the same board
/// twice went 17.9s to 0.0s, three asked to three remembered.
/// </remarks>
[Collection(PooledSqliteTests.Name)]
public class AiEstimateStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ai_est_{Guid.NewGuid():N}.db");
    private readonly DateTimeOffset _now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private AiEstimateStore Store() => new(_dbPath);

    private static AiEstimateItem Item(string id, string title) => new(id, title, null, null);

    [Fact]
    public void The_same_product_typed_differently_is_the_same_question()
    {
        // The key is the product, not the punctuation somebody typed around it.
        Assert.Equal(AiEstimateStore.KeyOf("Razor Moped"), AiEstimateStore.KeyOf("razor  moped!!"));
        Assert.Equal(AiEstimateStore.KeyOf("2016 Ford Fusion SEL"), AiEstimateStore.KeyOf("2016 FORD FUSION - SEL"));
        // But two different products are not.
        Assert.NotEqual(AiEstimateStore.KeyOf("Razor Moped"), AiEstimateStore.KeyOf("Razor Scooter"));
    }

    [Fact]
    public void What_was_saved_comes_back_without_asking_the_model()
    {
        var store = Store();
        var asked = new[] { Item("a", "Razor Moped"), Item("b", "Milwaukee 1107-1 Right Angle Drill") };
        store.Save(asked, [new AiResaleEstimate("a", 50m, 150m, "used electric moped"),
                           new AiResaleEstimate("b", 80m, 150m, "pro tool, durable")], _now);

        var known = store.Known(asked, _now);

        Assert.Equal(2, known.Count);
        Assert.Equal(100m, known["a"].Mid);
        Assert.Equal("used electric moped", known["a"].Basis);
    }

    [Fact]
    public void A_relisted_item_hits_the_cache_under_its_new_id()
    {
        // The whole reason the Marketplace item id is not the key: the same moped comes back
        // tomorrow with a new id, and a cache keyed on that would never once hit.
        var store = Store();
        store.Save([Item("old-id", "Razor Moped")], [new AiResaleEstimate("old-id", 50m, 150m, "moped")], _now);

        var known = store.Known([Item("brand-new-id", "razor moped")], _now);

        Assert.True(known.ContainsKey("brand-new-id"));
        Assert.Equal(100m, known["brand-new-id"].Mid);
    }

    [Fact]
    public void An_estimate_older_than_a_month_is_asked_again()
    {
        // Second-hand prices move. Not by the hour — but a year-old estimate of a phone is wrong.
        var store = Store();
        store.Save([Item("a", "Razor Moped")], [new AiResaleEstimate("a", 50m, 150m, "moped")], _now);

        Assert.Single(store.Known([Item("a", "Razor Moped")], _now + AiEstimateStore.GoodFor - TimeSpan.FromDays(1)));
        Assert.Empty(store.Known([Item("a", "Razor Moped")], _now + AiEstimateStore.GoodFor + TimeSpan.FromDays(1)));
    }

    [Fact]
    public void The_newest_answer_replaces_the_older_one()
    {
        var store = Store();
        store.Save([Item("a", "Razor Moped")], [new AiResaleEstimate("a", 50m, 150m, "first")], _now);
        store.Save([Item("a", "Razor Moped")], [new AiResaleEstimate("a", 70m, 190m, "second")], _now.AddDays(1));

        var known = store.Known([Item("a", "Razor Moped")], _now.AddDays(1));

        Assert.Equal("second", known["a"].Basis);
        Assert.Equal((1, 1), store.Counts(_now.AddDays(1)));   // one row, not two
    }

    [Fact]
    public void A_broken_answer_is_never_written_down()
    {
        var store = Store();
        store.Save([Item("a", "Thing"), Item("b", "Other")],
                   [new AiResaleEstimate("a", 0m, 0m, "zero"), new AiResaleEstimate("b", -5m, 10m, "negative")],
                   _now);

        Assert.Empty(store.Known([Item("a", "Thing"), Item("b", "Other")], _now));
    }

    [Fact]
    public void The_endpoint_asks_the_model_only_for_what_the_database_did_not_know()
    {
        var program = ReadSource("ING eBay AutoLister/Program.cs");
        var endpoint = Slice(program, "app.MapPost(\"/api/local/ai-estimate\"", "app.MapPost(\"/api/local/price-these\"");

        Assert.Contains("estimateStore.Known(items, now)", endpoint, StringComparison.Ordinal);
        Assert.Contains("items.Where(i => !cached.ContainsKey(i.ItemId ?? \"\")).ToList()", endpoint, StringComparison.Ordinal);
        Assert.Contains("claude.EstimateResaleAsync(missing, ct)", endpoint, StringComparison.Ordinal);
        Assert.Contains("estimateStore.Save(missing, fresh, now)", endpoint, StringComparison.Ordinal);
        // A board whose every item is already known never reaches the model at all.
        Assert.Contains("if (missing.Count == 0)", endpoint, StringComparison.Ordinal);
        // And an AI outage still returns whatever was already remembered.
        Assert.Contains("estimates = cached.Values.ToList(), fromCache = cached.Count,", endpoint, StringComparison.Ordinal);
    }

    private static string Slice(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{from}' is gone");
        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, $"'{to}' never closes '{from}'");
        return text[start..end];
    }

    private static string ReadSource(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root");
        return File.ReadAllText(Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* a temp file that outlives the run is not a failure */ }
        GC.SuppressFinalize(this);
    }
}
