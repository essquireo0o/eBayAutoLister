using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// This is the only guard in the app standing between a lost response and two live eBay listings for
// one physical item — two insertion fees, two audiences, and an oversell as soon as one sells. Each
// test below is a way that could happen.
[Collection(PooledSqliteTests.Name)]
public class PublishGuardTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"publish_guard_{Guid.NewGuid():N}.db");

    private WorkRecoveryStore NewStore() => new(_dbPath);
    private PublishGuard NewGuard(WorkRecoveryStore store) => new(store, new ActionLog());

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    private static PostListingRequest Listing(string title = "Antminer S19 Pro 110TH", decimal price = 1200m) => new()
    {
        Title = title,
        Price = price,
        Quantity = 1,
        CategoryId = "179171",
        Condition = "USED_EXCELLENT",
        ListingFormat = "FIXED_PRICE",
    };

    // ── Fingerprint ─────────────────────────────────────────────────────────

    [Fact]
    public void The_same_listing_fingerprints_the_same_way_twice()
        => Assert.Equal(PublishGuard.Fingerprint(Listing()), PublishGuard.Fingerprint(Listing()));

    [Fact]
    public void A_different_price_is_a_different_listing()
        => Assert.NotEqual(PublishGuard.Fingerprint(Listing(price: 1200m)),
                           PublishGuard.Fingerprint(Listing(price: 1300m)));

    [Fact]
    public void A_different_title_is_a_different_listing()
        => Assert.NotEqual(PublishGuard.Fingerprint(Listing("Antminer S19")),
                           PublishGuard.Fingerprint(Listing("Antminer S19 Pro")));

    [Fact]
    public void A_different_quantity_is_a_different_listing()
    {
        var one = Listing();
        var two = Listing();
        two.Quantity = 5;

        Assert.NotEqual(PublishGuard.Fingerprint(one), PublishGuard.Fingerprint(two));
    }

    // Trailing whitespace is not a decision to list something different, and treating it as one would
    // let a stray space defeat the duplicate guard entirely.
    [Fact]
    public void Trailing_whitespace_in_the_title_does_not_change_the_fingerprint()
        => Assert.Equal(PublishGuard.Fingerprint(Listing("Antminer S19 Pro 110TH")),
                        PublishGuard.Fingerprint(Listing("  Antminer S19 Pro 110TH  ")));

    // The publish path uploads photos to eBay's own servers first, so the same listing legitimately
    // carries different image URLs on a second attempt. Folding them in would make every retry look
    // like new content and defeat the whole guard.
    [Fact]
    public void Photo_urls_are_not_part_of_the_fingerprint()
    {
        var local = Listing();
        local.ImageUrls = ["http://localhost:9332/generated-photos/a.jpg"];
        var uploaded = Listing();
        uploaded.ImageUrls = ["https://i.ebayimg.com/images/g/abc/s-l1600.jpg"];

        Assert.Equal(PublishGuard.Fingerprint(local), PublishGuard.Fingerprint(uploaded));
    }

    // ── The in-flight lease ─────────────────────────────────────────────────

    [Fact]
    public void The_first_publish_of_a_listing_proceeds()
    {
        var guard = NewGuard(NewStore());

        Assert.Equal(PublishDecision.Proceed, guard.Begin(PublishGuard.Fingerprint(Listing()), null).Decision);
    }

    // The double-click, and the impatient second press while the first is still running.
    [Fact]
    public void A_second_identical_publish_while_the_first_is_running_is_refused()
    {
        var guard = NewGuard(NewStore());
        var fingerprint = PublishGuard.Fingerprint(Listing());

        guard.Begin(fingerprint, null);
        var second = guard.Begin(fingerprint, null);

        Assert.Equal(PublishDecision.AlreadyRunning, second.Decision);
    }

    [Fact]
    public void A_different_listing_is_not_blocked_by_one_already_running()
    {
        var guard = NewGuard(NewStore());

        guard.Begin(PublishGuard.Fingerprint(Listing("First item")), null);
        var other = guard.Begin(PublishGuard.Fingerprint(Listing("Second item")), null);

        Assert.Equal(PublishDecision.Proceed, other.Decision);
    }

    [Fact]
    public void The_lease_is_released_when_the_publish_finishes_so_a_relist_is_still_possible()
    {
        var store = NewStore();
        var guard = NewGuard(store);
        var fingerprint = PublishGuard.Fingerprint(Listing());

        guard.Begin(fingerprint, null);
        guard.Failed(fingerprint, null, "eBay refused it");

        // Failed rather than Succeeded on purpose: after a rejection the seller fixes the listing and
        // publishes again, and a lease that outlived the attempt would block them.
        Assert.Equal(PublishDecision.Proceed, guard.Begin(fingerprint, null).Decision);
    }

    // ── The recent-publish record ───────────────────────────────────────────

    [Fact]
    public void Republishing_a_listing_that_just_went_live_returns_the_existing_listing()
    {
        var store = NewStore();
        var guard = NewGuard(store);
        var fingerprint = PublishGuard.Fingerprint(Listing());

        guard.Begin(fingerprint, null);
        guard.Succeeded(fingerprint, null, "1234567890");

        var again = guard.Begin(fingerprint, null);

        Assert.Equal(PublishDecision.AlreadyPublished, again.Decision);
        Assert.Equal("1234567890", again.ListingId);
    }

    // Sellers do legitimately list two of the same item. Past the window their intent is taken at
    // face value rather than second-guessed.
    [Fact]
    public void An_older_publish_of_the_same_content_does_not_block_a_deliberate_relist()
    {
        var store = NewStore();
        var fingerprint = PublishGuard.Fingerprint(Listing());
        store.RecordPublished(null, fingerprint, "1111111111");

        Assert.Null(store.FindPublished(fingerprint, TimeSpan.Zero));
        Assert.NotNull(store.FindPublished(fingerprint, TimeSpan.FromMinutes(30)));
    }

    // The protection must not depend on the client having remembered to send a draft key — a publish
    // from a path that never autosaved is exactly as capable of being duplicated.
    [Fact]
    public void The_duplicate_record_is_written_even_with_no_draft_key()
    {
        var store = NewStore();
        var guard = NewGuard(store);
        var fingerprint = PublishGuard.Fingerprint(Listing());

        guard.Succeeded(fingerprint, workKey: null, listingId: "9999");

        Assert.Equal("9999", store.FindPublished(fingerprint, TimeSpan.FromMinutes(30))?.ListingId);
    }

    // ── Reconciliation ──────────────────────────────────────────────────────

    private static EbayListingSummary Active(string title, string id, DateTime? started = null) => new()
    {
        Title = title,
        ListingId = id,
        StartTimeUtc = started,
        Price = 1200m,
    };

    [Fact]
    public void A_listing_that_went_live_despite_the_error_is_found_by_its_title()
    {
        var now = DateTimeOffset.UtcNow;
        var active = new[] { Active("Something else", "1"), Active("Antminer S19 Pro 110TH", "2", now.UtcDateTime) };

        var match = PublishGuard.MatchPublished(active, "Antminer S19 Pro 110TH", now, TimeSpan.FromMinutes(30));

        Assert.Equal("2", match?.ListingId);
    }

    [Fact]
    public void Matching_ignores_case_and_surrounding_whitespace()
    {
        var now = DateTimeOffset.UtcNow;
        var active = new[] { Active("antminer s19 pro 110th", "7", now.UtcDateTime) };

        Assert.Equal("7", PublishGuard.MatchPublished(active, "  Antminer S19 Pro 110TH ", now, TimeSpan.FromMinutes(30))?.ListingId);
    }

    // The important negative: a seller relisting something they sold last month must not be told
    // their new listing already exists, which would leave them with nothing live at all.
    [Fact]
    public void An_old_listing_with_the_same_title_is_not_mistaken_for_the_one_just_published()
    {
        var now = DateTimeOffset.UtcNow;
        var active = new[] { Active("Antminer S19 Pro 110TH", "5", now.AddDays(-40).UtcDateTime) };

        Assert.Null(PublishGuard.MatchPublished(active, "Antminer S19 Pro 110TH", now, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void No_match_when_nothing_on_the_account_has_that_title()
    {
        var now = DateTimeOffset.UtcNow;
        var active = new[] { Active("A completely different item", "1", now.UtcDateTime) };

        Assert.Null(PublishGuard.MatchPublished(active, "Antminer S19 Pro 110TH", now, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void An_empty_title_matches_nothing_rather_than_the_first_listing_it_finds()
    {
        var now = DateTimeOffset.UtcNow;
        var active = new[] { Active("", "1", now.UtcDateTime), Active("Real item", "2", now.UtcDateTime) };

        Assert.Null(PublishGuard.MatchPublished(active, "", now, TimeSpan.FromMinutes(30)));
        Assert.Null(PublishGuard.MatchPublished(active, null, now, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void A_listing_with_no_id_is_never_offered_as_the_match()
    {
        var now = DateTimeOffset.UtcNow;
        var active = new[] { Active("Antminer S19 Pro 110TH", "", now.UtcDateTime) };

        Assert.Null(PublishGuard.MatchPublished(active, "Antminer S19 Pro 110TH", now, TimeSpan.FromMinutes(30)));
    }

    // eBay does not always report a start time. Excluding those rows would silently defeat
    // reconciliation on exactly the accounts where it is needed, so an unknown age is included and
    // the title carries the match.
    [Fact]
    public void A_listing_whose_age_eBay_did_not_report_is_still_considered()
    {
        var now = DateTimeOffset.UtcNow;
        var active = new[] { Active("Antminer S19 Pro 110TH", "3", started: null) };

        Assert.Equal("3", PublishGuard.MatchPublished(active, "Antminer S19 Pro 110TH", now, TimeSpan.FromMinutes(30))?.ListingId);
    }

    [Fact]
    public void The_newest_match_wins_when_several_share_a_title()
    {
        var now = DateTimeOffset.UtcNow;
        var active = new[]
        {
            Active("Same title", "older", now.AddMinutes(-20).UtcDateTime),
            Active("Same title", "newer", now.AddMinutes(-1).UtcDateTime),
        };

        Assert.Equal("newer", PublishGuard.MatchPublished(active, "Same title", now, TimeSpan.FromMinutes(30))?.ListingId);
    }

    // ── The publish whose draft never got saved ─────────────────────────────
    //
    // The seller whose autosave is failing is the one who most needs the journal, and they were the
    // one seller who had none: the guard only ever updated an existing row, so a publish with no
    // draft behind it went unrecorded. The two tests below are the end-to-end version of that — the
    // guard is handed a key that is not in the store, exactly as the endpoint hands it one.

    [Fact]
    public void A_publish_with_no_saved_draft_still_leaves_a_row_the_seller_can_resolve()
    {
        var store = NewStore();
        var guard = NewGuard(store);

        guard.Begin(PublishGuard.Fingerprint(Listing()), "wip-never-saved", "Antminer S19 Pro 110TH");

        var row = Assert.Single(store.Unresolved());
        Assert.Equal(WorkStage.Publishing, row.Stage);
        // The title has to reach the row, because Check eBay reconciles by matching on it — the same
        // matching MatchPublished does above. A journal row with no title cannot be resolved.
        Assert.Equal("Antminer S19 Pro 110TH", row.Label);
    }

    [Fact]
    public void A_failure_with_no_saved_draft_is_reported_rather_than_dropped()
    {
        var store = NewStore();
        var guard = NewGuard(store);
        var fingerprint = PublishGuard.Fingerprint(Listing());

        guard.Begin(fingerprint, "wip-never-saved", "Antminer S19 Pro 110TH");
        guard.Failed(fingerprint, "wip-never-saved", "eBay rejected the category", "Antminer S19 Pro 110TH");

        var row = Assert.Single(store.Recoverable());
        Assert.Equal(WorkStage.Failed, row.Stage);
        Assert.Equal("Antminer S19 Pro 110TH", row.Label);
        Assert.Contains("category", row.LastError);
    }

    // A failure has to release the lease as well as record itself, or the retry the seller makes
    // after fixing the problem is refused as a duplicate of the attempt that never worked.
    [Fact]
    public void A_journalled_failure_still_lets_the_seller_try_again()
    {
        var store = NewStore();
        var guard = NewGuard(store);
        var fingerprint = PublishGuard.Fingerprint(Listing());

        guard.Begin(fingerprint, "wip-never-saved", "Antminer S19 Pro 110TH");
        guard.Failed(fingerprint, "wip-never-saved", "eBay rejected the category", "Antminer S19 Pro 110TH");

        Assert.Equal(PublishDecision.Proceed, guard.Begin(fingerprint, "wip-never-saved", "Antminer S19 Pro 110TH").Decision);
    }
}
