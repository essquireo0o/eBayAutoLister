using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// What the app remembers about where the seller files things. Two properties matter here and
// nothing else does: it must not grow without limit on somebody's disk, and it must never learn
// something that would produce an unpublishable suggestion later.
public class CategoryMemoryStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cat_memory_{Guid.NewGuid():N}.db");

    private CategoryMemoryStore NewStore() => new(_dbPath);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Remembers_a_published_category_and_reads_it_back()
    {
        var store = NewStore();
        store.Remember("Bitmain Antminer S19 Pro 110TH", "179171", "Miners");

        var uses = store.Recent();

        Assert.Single(uses);
        Assert.Equal("179171", uses[0].CategoryId);
        Assert.Equal("Miners", uses[0].CategoryName);
        Assert.Equal(1, uses[0].UseCount);
    }

    [Fact]
    public void Listing_the_same_thing_again_counts_up_rather_than_piling_up()
    {
        // A seller who lists one model forty times should leave one row saying forty, not forty
        // rows saying the same sentence — and the count is what makes the suggestion say "where
        // you put 40 listings like this".
        var store = NewStore();
        store.Remember("Bitmain Antminer S19 Pro 110TH Miner", "179171", "Miners");
        store.Remember("antminer s19 pro (110th) bitmain miner", "179171", "Miners");

        var uses = store.Recent();

        Assert.Single(uses);
        Assert.Equal(2, uses[0].UseCount);
    }

    [Fact]
    public void The_same_title_under_two_categories_is_kept_as_two_facts()
    {
        // Both are true — the seller did file it both ways. Deciding between them is the
        // suggester's job, and it refuses; the store must not throw one of them away first.
        var store = NewStore();
        store.Remember("Antminer Hash Board", "179171", "Miners");
        store.Remember("Antminer Hash Board", "64800", "Electronic Components");

        Assert.Equal(2, store.Count());
    }

    [Fact]
    public void A_category_id_that_is_not_a_number_is_never_learned()
    {
        // eBay category IDs are numeric. A display name that has landed in the ID field is a bug
        // upstream, and remembering it would teach the app to suggest something that cannot publish.
        var store = NewStore();
        store.Remember("Bitmain Antminer S19", "Miners", "Miners");
        store.Remember("Bitmain Antminer S19", "", "Miners");
        store.Remember("Bitmain Antminer S19", "179171 ", "Miners");   // trimmed, and kept

        var uses = store.Recent();
        Assert.Single(uses);
        Assert.Equal("179171", uses[0].CategoryId);
    }

    [Fact]
    public void A_title_with_nothing_in_it_teaches_nothing()
    {
        var store = NewStore();
        store.Remember("", "179171", "Miners");
        store.Remember(null, "179171", "Miners");
        store.Remember("New Sealed Lot Free Shipping", "179171", "Miners");   // all filler words

        Assert.Equal(0, store.Count());
    }

    [Fact]
    public void A_blank_name_never_overwrites_one_the_app_already_knew()
    {
        // The ID publishes, but the name is what the seller reads on the chip when the suggestion
        // is offered back. Losing it turns "Miners" into "Category 179171".
        var store = NewStore();
        store.Remember("Bitmain Antminer S19 Pro", "179171", "Miners");
        store.Remember("Bitmain Antminer S19 Pro", "179171", "");

        var uses = store.Recent();
        Assert.Single(uses);
        Assert.Equal("Miners", uses[0].CategoryName);
        Assert.Equal(2, uses[0].UseCount);
    }

    [Fact]
    public void The_most_recently_used_category_is_read_back_first()
    {
        var store = NewStore();
        store.Remember("Canon EOS R6 Mirrorless Camera", "31388", "Digital Cameras");
        store.Remember("Bitmain Antminer S19 Pro Miner", "179171", "Miners");

        Assert.Equal("179171", store.Recent()[0].CategoryId);
    }

    [Fact]
    public void The_table_stops_growing_at_its_cap()
    {
        // This is a convenience. It is not allowed to become a file that grows forever on the
        // seller's machine because they list a lot, which is the case it is built for.
        var store = NewStore();
        for (var i = 0; i < CategoryMemoryStore.MaxRows + 25; i++)
            store.Remember($"Widget Model AB{i} Assembly", "179171", "Miners");

        Assert.Equal(CategoryMemoryStore.MaxRows, store.Count());

        // And what survives is the newest, not an arbitrary slice.
        var newest = store.Recent(1);
        Assert.Single(newest);
        Assert.Contains($"AB{CategoryMemoryStore.MaxRows + 24}", newest[0].Title, StringComparison.Ordinal);
    }

    [Fact]
    public void What_it_remembers_is_what_the_suggester_can_read()
    {
        // The two halves are useless apart: the store writes rows keyed on CategorySuggester.Key,
        // and the suggester ranks them by the same tokens. This is the seam between them.
        var store = NewStore();
        store.Remember("Bitmain Antminer S19 Pro 110TH Bitcoin Miner", "179171", "Miners");

        var match = CategorySuggester.FromHistory(
            "Bitmain Antminer S19 Pro 110TH Bitcoin Miner", store.Recent());

        Assert.NotNull(match);
        Assert.Equal("179171", match.CategoryId);
        Assert.Equal("Miners", match.CategoryName);
    }
}
