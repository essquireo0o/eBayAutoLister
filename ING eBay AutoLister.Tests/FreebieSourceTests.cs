using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;
using Xunit;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The freebie source's own plumbing: what survives the classifier, how a surviving row is rewritten
/// onto this board, and the two shared behaviours this feature changed for everybody
/// (craigslist's category and the cross-source dedupe).
/// </summary>
public class FreebieSourceTests
{
    private static readonly DateTime Now = new(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);

    // ItemId derives from the URL so two candidates in one test are two posts — LocalSupplyResults
    // dedupes on (source, id), and a shared id would silently collapse them.
    private static LocalSupplyListing Candidate(string title, decimal? price = null, string url = "https://x/1") => new()
    {
        Source = "craigslist", SourceLabel = "Craigslist", ItemId = url, Url = url, Title = title, Price = price,
    };

    // ── What reaches the board ───────────────────────────────────────────────────────────────────

    [Fact]
    public void A_surviving_row_is_rewritten_onto_the_freebie_board()
    {
        var kept = FreebieSourceService.KeepFreebies([Candidate("Free Kenmore Stove")], fromFreeBoard: true, Now);

        var row = Assert.Single(kept);
        Assert.Equal(FreebieCatalog.SourceId, row.Source);
        Assert.Equal("Free — local pickup", row.SourceLabel);
        // The comp lookup has to see the product, not the offer wrapped around it.
        Assert.Equal("Kenmore Stove", row.Title);
        Assert.Equal(0m, row.Price);
        Assert.True(row.IsFree);
        Assert.NotNull(row.Freebie);
        // A stranger giving away a stove is not a retail purchase, whatever the feed parser thought.
        Assert.False(row.IsRetail);
    }

    [Fact]
    public void Everything_the_classifier_refuses_is_dropped_rather_than_kept_unpriced()
    {
        var candidates = new[]
        {
            Candidate("Free firewood pine tree wood"),
            Candidate("Free 75'' flatscreen Hisense TV - Damaged panel/screen"),
            Candidate("FREE Curb Alert - Help Yourself!"),
            Candidate("Leghorn Rooster"),
            Candidate("Free Kenmore Stove"),
        };

        var kept = FreebieSourceService.KeepFreebies(candidates, fromFreeBoard: true, Now);

        Assert.Single(kept);
        Assert.Equal("Kenmore Stove", kept[0].Title);
    }

    [Fact]
    public void A_row_whose_title_is_nothing_but_the_word_free_is_dropped()
    {
        // "Free" cleans to an empty string, and an empty title cannot be comped.
        Assert.Empty(FreebieSourceService.KeepFreebies([Candidate("FREE")], fromFreeBoard: true, Now));
    }

    [Fact]
    public void A_near_free_row_keeps_its_real_price_and_is_not_marked_free()
    {
        var kept = FreebieSourceService.KeepFreebies(
            [Candidate("Fender Celluloid Picks 12-pack", price: 3.99m)], fromFreeBoard: false, Now);

        var row = Assert.Single(kept);
        Assert.Equal(3.99m, row.Price);
        Assert.False(row.IsFree);
        Assert.Equal(FreebieKinds.NearFree, row.Freebie!.Kind);
    }

    // ── Result assembly ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_local_free_board_is_never_emptied_by_a_keyword_that_matches_nothing()
    {
        // A seller who asked for "desk" and is shown a free filing cabinet two miles away has been
        // done a favour, not shown a false match. FilterByRelevance falls back rather than report a
        // false empty.
        var local = FreebieSourceService.KeepFreebies(
            [Candidate("Free Kenmore Stove"), Candidate("Free Oak wall unit", url: "https://x/2")],
            fromFreeBoard: true, Now);

        var result = FreebieSourceService.BuildResult(local, [], "desk", "Las Vegas");

        Assert.Equal(2, result.Count);
        Assert.Equal("ok", result.Status);
        Assert.Contains("Las Vegas", result.ScopeLabel);
    }

    [Fact]
    public void The_result_leads_with_the_outright_freebies()
    {
        var items = FreebieSourceService.KeepFreebies(
            [Candidate("Fender Celluloid Picks 12-pack", 3.99m), Candidate("Free Kenmore Stove", url: "https://x/2")],
            fromFreeBoard: false, Now);

        var result = FreebieSourceService.BuildResult(items, [], "", "");

        // Cheapest first, which on this board means the $0 rows — also the order the analysis cap
        // is spent in.
        Assert.Equal(0m, result.Items[0].Price);
    }

    // ── Craigslist's free board, reached without a second copy of the service ───────────────────

    [Fact]
    public void The_free_stuff_category_goes_into_the_search_url()
    {
        var url = CraigslistParser.BuildSearchUrl(
            "lasvegas", "tv", "89101", 40, rss: false, category: FreebieCatalog.CraigslistFreeCategory);

        Assert.Contains("/search/zip?", url);
        Assert.Contains("postal=89101", url);
        Assert.Contains("search_distance=40", url);
    }

    [Fact]
    public void Every_existing_caller_still_searches_the_for_sale_board()
    {
        Assert.Contains("/search/sss?", CraigslistParser.BuildSearchUrl("lasvegas", "tv", "89101", 40));
    }

    [Fact]
    public void A_category_that_is_not_one_of_craigslists_own_codes_is_ignored()
    {
        // It goes into the URL path; craigslist's own short lowercase vocabulary is the only thing
        // that belongs there.
        Assert.Contains("/search/sss?", CraigslistParser.BuildSearchUrl("lasvegas", "tv", "89101", 40, category: "../../evil"));
        Assert.Contains("/search/sss?", CraigslistParser.BuildSearchUrl("lasvegas", "tv", "89101", 40, category: ""));
    }

    [Fact]
    public void On_the_free_board_a_post_with_no_price_is_free_rather_than_unreadable()
    {
        const string html = """
            <li class="cl-static-search-result" title="Oak wall unit">
              <a href="https://lasvegas.craigslist.org/fuo/d/oak/7891234567.html">
                <div class="title">Oak wall unit</div>
                <div class="location">Henderson</div>
              </a>
            </li>
            """;

        // Off the free board this post has no cost basis and is dropped, exactly as before.
        Assert.Empty(CraigslistParser.ParseStaticHtml(html));

        // On it, "no price" means free — and without this the parser throws away most of the best
        // supply this feature has, because most free posts never say the word.
        var free = Assert.Single(CraigslistParser.ParseStaticHtml(html, freeBoard: true));
        Assert.True(free.IsFree);
        Assert.Equal("Oak wall unit", free.Title);
    }

    // ── One post, one row ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_same_post_found_by_two_sources_appears_once()
    {
        // A free craigslist post is on the for-sale board AND the free-stuff board, and the two
        // arrive under different source ids — which the per-source dedupe cannot see.
        var merged = LocalSupplyMerger.Merge(
        [
            new LocalSupplySearchResult
            {
                SourceId = "craigslist", Status = "ok",
                Items = [new LocalSupplyListing { Source = "craigslist", ItemId = "7891234567", Url = "https://cl/7891234567.html", Title = "Free couch", IsFree = true }],
            },
            new LocalSupplySearchResult
            {
                SourceId = FreebieCatalog.SourceId, Status = "ok",
                Items = [new LocalSupplyListing { Source = FreebieCatalog.SourceId, ItemId = "7891234567", Url = "https://cl/7891234567.html", Title = "couch", IsFree = true }],
            },
        ], "couch", "89101", 40);

        Assert.Equal(1, merged.Count);
        // First source wins, which is registration order.
        Assert.Equal("craigslist", merged.Items[0].Source);
    }

    [Fact]
    public void Two_different_posts_are_never_collapsed_into_one()
    {
        var merged = LocalSupplyMerger.Merge(
        [
            new LocalSupplySearchResult
            {
                SourceId = FreebieCatalog.SourceId, Status = "ok",
                Items =
                [
                    new LocalSupplyListing { Source = FreebieCatalog.SourceId, ItemId = "1", Url = "https://cl/1.html", Title = "Free couch" },
                    new LocalSupplyListing { Source = FreebieCatalog.SourceId, ItemId = "2", Url = "https://cl/2.html", Title = "Free couch" },
                    // No URL at all: kept, never treated as a duplicate of the other blank one.
                    new LocalSupplyListing { Source = FreebieCatalog.SourceId, ItemId = "3", Title = "Free couch" },
                    new LocalSupplyListing { Source = FreebieCatalog.SourceId, ItemId = "4", Title = "Free couch" },
                ],
            },
        ], "couch", "89101", 40);

        Assert.Equal(4, merged.Count);
    }

    // ── How the source describes itself ──────────────────────────────────────────────────────────

    [Fact]
    public void The_source_is_public_local_taxed_and_the_only_one_that_takes_a_blank_search()
    {
        // Through the interface, because these are default interface members — which is the point:
        // no existing source had to be edited to gain them.
        ILocalSupplySource source = new FreebieSourceService(
            new StubHttpClientFactory(), new CraigslistService(new StubHttpClientFactory(), new ActionLog()), new ActionLog());

        Assert.Equal("freebies", source.Id);
        Assert.False(source.RequiresConnection);
        Assert.True(source.IsAvailable);
        // Half of it is a board in the seller's own metro, so the zip is real.
        Assert.True(source.IsLocationBased);
        // A rebate deal is rung up at a till, and the refund never covers the tax.
        Assert.True(source.ChargesSalesTax);
        Assert.True(source.AllowsBlankQuery);
        // Freecycle and Buy Nothing have no public search to read, so they are offered as links.
        Assert.Equal(2, source.ManualSites.Count);
    }

    [Fact]
    public void No_other_source_claims_a_blank_search_is_meaningful()
    {
        ILocalSupplySource feeds = new DealFeedService(new StubHttpClientFactory(), new ActionLog());
        ILocalSupplySource classifieds = new CraigslistService(new StubHttpClientFactory(), new ActionLog());

        Assert.False(feeds.AllowsBlankQuery);
        Assert.False(classifieds.AllowsBlankQuery);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
