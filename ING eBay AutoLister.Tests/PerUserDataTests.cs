using System.Net;
using System.Net.Http.Json;
using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// Proves the second half of what makes a hosted deployment safe to open to a second person: not
/// just that they cannot use each other's eBay account, but that they cannot see each other's
/// money.
/// </summary>
/// <remarks>
/// <para>
/// Flips, listings, cost basis and deals all lived in one SQLite file with no column saying whose
/// row was whose, because for the app's whole life there was one seller and one machine. Put that
/// on a server and the second person to sign up opens Money Made and reads the first person's
/// revenue, their costs and their margins — and can delete a sale out of it.
/// </para>
/// <para>
/// The store tests below drive ONE store instance while changing who is signed in, because that is
/// what the app really has: the stores are singletons and the user is resolved per request. A test
/// that built a second store for the second user would pass just as happily against a store that
/// read the whole table.
/// </para>
/// </remarks>
[Collection(PooledSqliteTests.Name)]
public class PerUserDataTests : IDisposable
{
    private const long UserA = 1;
    private const long UserB = 2;

    private const string Password = "a-long-enough-password";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"per_user_data_{Guid.NewGuid():N}.db");

    /// <summary>Who is signed in on the call in flight. Moved between assertions, never rebuilt.</summary>
    private long? _signedIn = UserA;

    /// <summary>The hosted build's scope: one object, an answer that changes per request.</summary>
    private UserScope Scope => UserScope.PerUser(() => _signedIn);

    // ── The acceptance: A cannot see B's earnings, through any method on the store ────────────

    [Fact]
    public void One_sellers_earnings_are_invisible_to_another_through_every_read()
    {
        var store = new EarningsStore(_dbPath, Scope);

        _signedIn = UserA;
        var mine = EbayLine(orderId: "ORD-A", price: 1000m, title: "Antminer S19");
        store.Upsert(mine);

        _signedIn = UserB;
        store.Upsert(EbayLine(orderId: "ORD-B", price: 25m, title: "A power cable"));

        // Every way the app can ask this store for a sale.
        var theirs = store.GetAll();
        Assert.Single(theirs);
        Assert.Equal("A power cable", theirs[0].Title);

        Assert.Null(store.Get(mine.Id));
        Assert.Null(store.FindByOrderLine("ORD-A", "LI-1"));
        Assert.Null(store.ApplyEdit(mine.Id, new FlipUpsertRequest { SalePrice = 1m }));
        Assert.False(store.Delete(mine.Id));

        // And none of that reaching over the fence damaged the row it could not see.
        _signedIn = UserA;
        var still = Assert.Single(store.GetAll());
        Assert.Equal("Antminer S19", still.Title);
        Assert.Equal(1000m, still.SalePrice);
    }

    [Fact]
    public void One_sellers_listings_are_invisible_to_another()
    {
        using var scratch = new ScratchApp(Scope);
        var listings = scratch.Listings;

        _signedIn = UserA;
        listings.SaveEdit(Edit(sku: "S19-001", title: "Antminer S19 95TH", price: 1200m));

        _signedIn = UserB;
        Assert.Equal(0, listings.GetStatus().ListingCount);

        // The same SKU — two sellers of the same model use the same manufacturer's code — must be
        // their own row, not an overwrite of the first seller's work.
        listings.SaveEdit(Edit(sku: "S19-001", title: "Their own S19", price: 900m));
        Assert.Equal(1, listings.GetStatus().ListingCount);

        _signedIn = UserA;
        Assert.Equal(1, listings.GetStatus().ListingCount);
        Assert.Equal("Antminer S19 95TH", scratch.TitleOf(UserA, "S19-001"));
        Assert.Equal("Their own S19", scratch.TitleOf(UserB, "S19-001"));
    }

    [Fact]
    public void One_sellers_cost_basis_is_invisible_to_another()
    {
        var store = new CostBasisStore(_dbPath, Scope);

        _signedIn = UserA;
        store.Save(new CostBasisEntry { ListingId = "110001", Sku = "S19-001", UnitCost = 800m });

        _signedIn = UserB;
        Assert.Empty(store.GetAll());
        Assert.Null(store.Find("110001", null));
        Assert.Null(store.Find(null, "S19-001"));
        Assert.False(store.Delete("110001", "S19-001"));

        // The same SKU again: the second seller records what THEY paid, and it is a second row.
        store.Save(new CostBasisEntry { ListingId = "110001", Sku = "S19-001", UnitCost = 500m });
        Assert.Equal(500m, store.Find("110001", null)!.UnitCost);

        _signedIn = UserA;
        Assert.Equal(800m, store.Find("110001", null)!.UnitCost);
    }

    [Fact]
    public void One_sellers_deals_are_invisible_to_another()
    {
        var store = new DealStore(_dbPath, Scope);

        _signedIn = UserA;
        var mine = DealStore.FromRequest(new DealUpsertRequest
        {
            Title = "Pallet of S19s", Source = "craigslist", SourceItemId = "CL-7788",
            Stage = DealStages.Sourced, AskPrice = 4000m, MaxBuyPrice = 3200m,
        });
        Assert.True(store.Upsert(mine));

        _signedIn = UserB;
        Assert.Empty(store.GetAll());
        Assert.Null(store.Get(mine.Id));
        Assert.Null(store.ApplyEdit(mine.Id, new DealUpsertRequest { Title = "Mine now" }));
        Assert.False(store.Delete(mine.Id));

        // Two sellers reading the same city's Craigslist see the same post. Tracking it is two
        // decisions about whether to buy it, so it is two cards — not one they fight over.
        var alsoTracked = DealStore.FromRequest(new DealUpsertRequest
        {
            Title = "Pallet of S19s", Source = "craigslist", SourceItemId = "CL-7788",
            Stage = DealStages.Sourced, AskPrice = 4000m, MaxBuyPrice = 2800m,
        });
        Assert.True(store.Upsert(alsoTracked));
        Assert.Equal(2800m, Assert.Single(store.GetAll()).MaxBuyPrice);

        _signedIn = UserA;
        var untouched = Assert.Single(store.GetAll());
        Assert.Equal(3200m, untouched.MaxBuyPrice);
        Assert.Equal("Pallet of S19s", untouched.Title);
    }

    // ── What the isolation must not break ────────────────────────────────────────────────────

    [Fact]
    public void Pressing_import_twice_still_does_not_double_one_sellers_earnings()
    {
        var store = new EarningsStore(_dbPath, Scope);

        _signedIn = UserA;
        store.Upsert(EbayLine(orderId: "ORD-1", price: 1000m));
        store.Upsert(EbayLine(orderId: "ORD-1", price: 1000m));

        Assert.Single(store.GetAll());
    }

    [Fact]
    public void Two_sellers_can_hold_the_same_ebay_order_id()
    {
        var store = new EarningsStore(_dbPath, Scope);

        // One person with two eBay accounts is enough for this to happen, and a unique index that
        // did not include the user would fail the second import with a constraint violation on a
        // screen that only says "importing".
        _signedIn = UserA;
        store.Upsert(EbayLine(orderId: "ORD-1", price: 1000m, title: "Theirs"));

        _signedIn = UserB;
        store.Upsert(EbayLine(orderId: "ORD-1", price: 40m, title: "Mine"));

        Assert.Equal(40m, Assert.Single(store.GetAll()).SalePrice);

        _signedIn = UserA;
        Assert.Equal(1000m, Assert.Single(store.GetAll()).SalePrice);
    }

    [Fact]
    public void Pressing_track_twice_still_does_not_duplicate_one_sellers_deal()
    {
        var store = new DealStore(_dbPath, Scope);
        _signedIn = UserA;

        DealRecord Tracked() => DealStore.FromRequest(new DealUpsertRequest
        {
            Title = "Pallet of S19s", Source = "craigslist", SourceItemId = "CL-7788",
            Stage = DealStages.Sourced, AskPrice = 4000m,
        });

        Assert.True(store.Upsert(Tracked()));
        Assert.False(store.Upsert(Tracked()));
        Assert.Single(store.GetAll());
    }

    // ── Background work, which has no user ───────────────────────────────────────────────────

    [Fact]
    public void A_background_job_with_no_signed_in_user_reads_nothing_and_writes_nothing()
    {
        using var scratch = new ScratchApp(Scope);
        var earnings  = new EarningsStore(_dbPath, Scope);
        var deals     = new DealStore(_dbPath, Scope);
        var costBasis = new CostBasisStore(_dbPath, Scope);

        _signedIn = UserA;
        earnings.Upsert(EbayLine(orderId: "ORD-A", price: 1000m));
        costBasis.Save(new CostBasisEntry { Sku = "S19-001", UnitCost = 800m });
        deals.Upsert(DealStore.FromRequest(new DealUpsertRequest
        {
            Title = "Pallet", Source = "craigslist", SourceItemId = "CL-1", Stage = DealStages.Sourced,
        }));
        scratch.Listings.SaveEdit(Edit(sku: "S19-001", title: "Antminer S19 95TH", price: 1200m));

        // The earnings import, the token refresh, the SEO job: no HttpContext, so no user.
        _signedIn = null;

        Assert.Empty(earnings.GetAll());
        Assert.Empty(deals.GetAll());
        Assert.Empty(costBasis.GetAll());
        Assert.Equal(0, scratch.Listings.GetStatus().ListingCount);

        // And what it tries to write goes nowhere rather than into the last seller who happened to
        // save. Picking one would be picking whose earnings to put a stranger's order in.
        Assert.False(earnings.Upsert(EbayLine(orderId: "ORD-STRAY", price: 5m, title: "Stray")));
        Assert.False(deals.Upsert(DealStore.FromRequest(new DealUpsertRequest
        {
            Title = "Stray deal", Source = "craigslist", SourceItemId = "CL-STRAY", Stage = DealStages.Sourced,
        })));
        costBasis.Save(new CostBasisEntry { Sku = "STRAY", UnitCost = 1m });
        Assert.Equal(0, costBasis.SaveMany([new CostBasisEntry { Sku = "STRAY-2", UnitCost = 1m }]));
        Assert.Equal(0, scratch.Listings.SaveEdit(Edit(sku: "STRAY", title: "Stray listing", price: 1m)).LocalId);

        _signedIn = UserA;
        Assert.Single(earnings.GetAll());
        Assert.Single(deals.GetAll());
        Assert.Single(costBasis.GetAll());
        Assert.Equal(1, scratch.Listings.GetStatus().ListingCount);
    }

    // ── The desktop build, which is not to change ────────────────────────────────────────────

    [Fact]
    public void The_desktop_build_still_sees_everything_the_one_seller_wrote()
    {
        using var scratch = new ScratchApp(UserScope.Desktop);
        var earnings  = new EarningsStore(_dbPath);
        var deals     = new DealStore(_dbPath);
        var costBasis = new CostBasisStore(_dbPath);

        earnings.Upsert(EbayLine(orderId: "ORD-1", price: 1000m));
        costBasis.Save(new CostBasisEntry { Sku = "S19-001", UnitCost = 800m });
        deals.Upsert(DealStore.FromRequest(new DealUpsertRequest
        {
            Title = "Pallet", Source = "craigslist", SourceItemId = "CL-1", Stage = DealStages.Sourced,
        }));
        scratch.Listings.SaveEdit(Edit(sku: "S19-001", title: "Antminer S19 95TH", price: 1200m));

        // No user id to think about, no sign-in, and the same numbers as before this change.
        Assert.Single(earnings.GetAll());
        Assert.Single(deals.GetAll());
        Assert.Equal(800m, costBasis.Find(null, "S19-001")!.UnitCost);
        Assert.Equal(1, scratch.Listings.GetStatus().ListingCount);
    }

    [Fact]
    public void Rows_written_before_this_change_still_belong_to_the_seller_who_wrote_them()
    {
        // A desktop database as it was: no user_id column anywhere, and a season's work in it.
        WritePreChangeDatabase();

        // Opening the stores runs the migration in place — the column is added with the desktop
        // owner as its default, which is exactly who entered every one of these rows.
        var earnings  = new EarningsStore(_dbPath);
        var costBasis = new CostBasisStore(_dbPath);
        var deals     = new DealStore(_dbPath);

        var flip = Assert.Single(earnings.GetAll());
        Assert.Equal("Antminer S19", flip.Title);
        Assert.Equal(1000m, flip.SalePrice);
        // The cost the seller typed months ago still prices the sale it was typed for.
        Assert.Equal(800m, costBasis.Find("110001", null)!.UnitCost);
        Assert.Equal("Pallet of S19s", Assert.Single(deals.GetAll()).Title);

        // And the row is still editable, which is what proves the migration left a usable row and
        // not just a readable one.
        Assert.NotNull(earnings.ApplyEdit(flip.Id, new FlipUpsertRequest { UnitCost = 800m }));
        Assert.Equal(800m, earnings.Get(flip.Id)!.UnitCost);
    }

    [Fact]
    public void A_migrated_database_lets_a_second_seller_use_the_keys_the_first_one_holds()
    {
        // The realistic hosted database is not a fresh one — it is the owner's own desktop file,
        // carried up to a server. It arrives with the OLD unique indexes on it, which are unique
        // across everybody: leave one in place and the second seller to import that order, record
        // that SKU or track that post gets a constraint failure instead of their own row.
        WritePreChangeDatabase();

        var earnings  = new EarningsStore(_dbPath, Scope);
        var costBasis = new CostBasisStore(_dbPath, Scope);
        var deals     = new DealStore(_dbPath, Scope);

        _signedIn = UserB;
        earnings.Upsert(EbayLine(orderId: "ORD-OLD", price: 40m, title: "Mine"));
        costBasis.Save(new CostBasisEntry { ListingId = "110001", Sku = "S19-001", UnitCost = 500m });
        deals.Upsert(DealStore.FromRequest(new DealUpsertRequest
        {
            Title = "Same pallet, my call", Source = "craigslist", SourceItemId = "CL-7788",
            Stage = DealStages.Sourced,
        }));

        Assert.Equal("Mine", Assert.Single(earnings.GetAll()).Title);
        Assert.Equal(500m, Assert.Single(costBasis.GetAll()).UnitCost);
        Assert.Equal("Same pallet, my call", Assert.Single(deals.GetAll()).Title);

        // And the rows that were already there — the ones the owner brought with them — are as
        // they were.
        _signedIn = UserScope.DesktopOwner;
        Assert.Equal(1000m, Assert.Single(earnings.GetAll()).SalePrice);
        Assert.Equal(800m, Assert.Single(costBasis.GetAll()).UnitCost);
        Assert.Equal("Pallet of S19s", Assert.Single(deals.GetAll()).Title);
    }

    // ── End to end, through two browsers ─────────────────────────────────────────────────────

    [Fact]
    public async Task Two_signed_in_sellers_see_two_different_boards()
    {
        await using var server = await StartAsync();
        var first  = await server.SignUpAsync("first@example.com");
        var second = await server.SignUpAsync("second@example.com");

        await first.LogSaleAsync("Antminer S19", 1000m);
        await first.TrackDealAsync("Pallet of S19s", "CL-7788");
        await first.SaveListingAsync("S19-001", "Antminer S19 95TH");

        await second.LogSaleAsync("A power cable", 25m);

        var theirs = await second.EarningsAsync();
        Assert.Equal("A power cable", Assert.Single(theirs).Title);
        Assert.Empty(await second.DealsAsync());
        Assert.Equal(0, await second.ListingCountAsync());

        var mine = await first.EarningsAsync();
        Assert.Equal("Antminer S19", Assert.Single(mine).Title);
        Assert.Equal("Pallet of S19s", Assert.Single(await first.DealsAsync()).Title);
        Assert.Equal(1, await first.ListingCountAsync());
    }

    [Fact]
    public async Task A_seller_cannot_delete_a_sale_they_cannot_see()
    {
        await using var server = await StartAsync();
        var first  = await server.SignUpAsync("first@example.com");
        var second = await server.SignUpAsync("second@example.com");

        await first.LogSaleAsync("Antminer S19", 1000m);
        var id = Assert.Single(await first.EarningsAsync()).Id;

        // Guessing a row id is the obvious try, and row ids are small integers.
        Assert.Equal(HttpStatusCode.NotFound, await second.DeleteSaleAsync(id));

        Assert.Equal(1000m, Assert.Single(await first.EarningsAsync()).SalePrice);
    }

    [Fact]
    public async Task A_seller_signing_back_in_still_has_their_own_board()
    {
        await using var server = await StartAsync();
        var seller = await server.SignUpAsync("seller@example.com");
        await seller.LogSaleAsync("Antminer S19", 1000m);

        // Per-user is not per-session: signing out is not a way to lose a season's earnings.
        var returning = await server.SignInAsync("seller@example.com");

        Assert.Equal("Antminer S19", Assert.Single(await returning.EarningsAsync()).Title);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────────────

    private static FlipRecord EbayLine(string orderId, decimal price, string title = "Antminer S19") => new()
    {
        Source = "ebay",
        OrderId = orderId,
        LineItemId = "LI-1",
        ListingId = "110001",
        Sku = "S19-001",
        Title = title,
        SoldUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        SalePrice = price,
    };

    private static UpdateListingRequest Edit(string sku, string title, decimal price) => new()
    {
        Sku = sku,
        Title = title,
        Price = price,
        Quantity = 1,
    };

    /// <summary>
    /// The tables exactly as they were before this change — no user_id, and the old shared unique
    /// indexes — with a season's work already in them.
    /// </summary>
    private void WritePreChangeDatabase()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE flips (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source TEXT NOT NULL DEFAULT 'manual',
                order_id TEXT NOT NULL DEFAULT '',
                line_item_id TEXT NOT NULL DEFAULT '',
                listing_id TEXT NOT NULL DEFAULT '',
                sku TEXT NOT NULL DEFAULT '',
                title TEXT NOT NULL DEFAULT '',
                sold_at TEXT NOT NULL DEFAULT '',
                quantity INTEGER NOT NULL DEFAULT 1,
                sale_price NUMERIC NOT NULL DEFAULT 0,
                shipping_charged NUMERIC NOT NULL DEFAULT 0,
                marketplace_fee NUMERIC NULL,
                shipping_cost NUMERIC NULL,
                other_costs NUMERIC NOT NULL DEFAULT 0,
                unit_cost NUMERIC NULL,
                refunded_amount NUMERIC NOT NULL DEFAULT 0,
                status TEXT NOT NULL DEFAULT 'paid',
                note TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL DEFAULT ''
            );

            CREATE UNIQUE INDEX ux_flips_order_line
                ON flips(order_id, line_item_id) WHERE order_id <> '' AND line_item_id <> '';

            INSERT INTO flips (source, order_id, line_item_id, listing_id, sku, title, sold_at,
                               quantity, sale_price, updated_at)
            VALUES ('ebay', 'ORD-OLD', 'LI-1', '110001', 'S19-001', 'Antminer S19',
                    '2026-05-01T00:00:00.0000000+00:00', 1, 1000, '2026-05-01T00:00:00.0000000+00:00');

            CREATE TABLE listing_cost_basis (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                listing_id TEXT NOT NULL DEFAULT '',
                sku TEXT NOT NULL DEFAULT '',
                unit_cost NUMERIC NOT NULL DEFAULT 0,
                inbound_shipping NUMERIC NOT NULL DEFAULT 0,
                note TEXT NOT NULL DEFAULT '',
                acquired_at TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL DEFAULT ''
            );

            CREATE UNIQUE INDEX ux_cost_basis_listing_id
                ON listing_cost_basis(listing_id) WHERE listing_id <> '';
            CREATE UNIQUE INDEX ux_cost_basis_sku
                ON listing_cost_basis(sku) WHERE sku <> '';

            INSERT INTO listing_cost_basis (listing_id, sku, unit_cost, updated_at)
            VALUES ('110001', 'S19-001', 800, '2026-05-01T00:00:00.0000000+00:00');

            CREATE TABLE deals (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                stage TEXT NOT NULL DEFAULT 'sourced',
                title TEXT NOT NULL DEFAULT '',
                source TEXT NOT NULL DEFAULT 'manual',
                source_label TEXT NOT NULL DEFAULT '',
                source_url TEXT NOT NULL DEFAULT '',
                source_item_id TEXT NOT NULL DEFAULT '',
                quantity INTEGER NOT NULL DEFAULT 1,
                ask_price NUMERIC NULL,
                max_buy_price NUMERIC NULL,
                projected_sale_price NUMERIC NULL,
                projected_net_profit NUMERIC NULL,
                projected_roi_percent NUMERIC NULL,
                projected_days_to_cash INTEGER NULL,
                projected_basis TEXT NOT NULL DEFAULT '',
                projected_at TEXT NOT NULL DEFAULT '',
                purchase_price NUMERIC NULL,
                purchase_extra_cost NUMERIC NOT NULL DEFAULT 0,
                bought_at TEXT NOT NULL DEFAULT '',
                listing_id TEXT NOT NULL DEFAULT '',
                sku TEXT NOT NULL DEFAULT '',
                listed_price NUMERIC NULL,
                listed_at TEXT NOT NULL DEFAULT '',
                sold_at TEXT NOT NULL DEFAULT '',
                note TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL DEFAULT '',
                stage_changed_at TEXT NOT NULL DEFAULT ''
            );

            CREATE UNIQUE INDEX ux_deals_source_item
                ON deals(source, source_item_id) WHERE source_item_id <> '';

            INSERT INTO deals (stage, title, source, source_item_id, quantity, created_at, updated_at,
                               stage_changed_at)
            VALUES ('sourced', 'Pallet of S19s', 'craigslist', 'CL-7788', 1,
                    '2026-05-01T00:00:00.0000000+00:00', '2026-05-01T00:00:00.0000000+00:00',
                    '2026-05-01T00:00:00.0000000+00:00');
            """;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A <see cref="ListingDatabase"/> over a throwaway content root — it names its own file, so it
    /// cannot share the one the other stores in a test are using.
    /// </summary>
    private sealed class ScratchApp : IDisposable
    {
        private readonly string _root;

        public ListingDatabase Listings { get; }

        public ScratchApp(UserScope scope)
        {
            _root = Path.Combine(Path.GetTempPath(), "ing-per-user-data", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = _root });
            builder.Logging.ClearProviders();
            Listings = new ListingDatabase(builder.Environment, scope);
        }

        /// <summary>What is actually on the row, read past the store — whose is the whole question.</summary>
        public string? TitleOf(long userId, string sku)
        {
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = Listings.DatabasePath }.ToString());
            connection.Open();
            using var select = connection.CreateCommand();
            select.CommandText = "SELECT title FROM local_listings WHERE user_id = @user_id AND sku = @sku LIMIT 1;";
            select.Parameters.AddWithValue("@user_id", userId);
            select.Parameters.AddWithValue("@sku", sku);
            return select.ExecuteScalar() as string;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder, not the point */ }
        }
    }

    // ── The server under test ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The app's own data and auth wiring around the endpoints Money Made, the pipeline board and
    /// the listing editor really post to, cut down to what these tests read back.
    /// </summary>
    private static async Task<DataServer> StartAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "ing-per-user-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = root,
            EnvironmentName = "Production",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddSingleton(new UserStore(Path.Combine(root, "App_Data", "users.db")));

        PerUserData.AddUserScope(builder, hosted: true);
        HostedAuth.AddAccounts(builder, hosted: true, secureCookie: false);

        builder.Services.AddSingleton<ListingDatabase>();
        builder.Services.AddSingleton<EarningsStore>();
        builder.Services.AddSingleton<CostBasisStore>();
        builder.Services.AddSingleton<DealStore>();

        var app = builder.Build();
        HostedAuth.UseSignedInUser(app, hosted: true);
        HostedAuth.RequireSignIn(app, hosted: true);
        HostedAuth.MapAccountEndpoints(app, hosted: true);

        app.MapGet("/api/earnings", (EarningsStore store) => Results.Ok(store.GetAll()));

        app.MapPost("/api/earnings/flips", (FlipUpsertRequest req, EarningsStore store) =>
        {
            store.Upsert(EarningsStore.FromRequest(req));
            return Results.Ok(store.GetAll());
        });

        app.MapDelete("/api/earnings/flips/{id:long}", (long id, EarningsStore store) =>
            store.Delete(id) ? Results.Ok() : Results.NotFound("That sale is no longer on record."));

        app.MapGet("/api/deals", (DealStore deals) => Results.Ok(deals.GetAll()));

        app.MapPost("/api/deals", (DealUpsertRequest req, DealStore deals) =>
        {
            deals.Upsert(DealStore.FromRequest(req));
            return Results.Ok(deals.GetAll());
        });

        app.MapGet("/api/local-db/status", (ListingDatabase db) => Results.Ok(db.GetStatus()));

        app.MapPost("/api/local-listings/save-edit", (UpdateListingRequest req, ListingDatabase db) =>
            Results.Ok(db.SaveEdit(req)));

        await app.StartAsync();
        return new DataServer(app, root);
    }

    private sealed record FlipRow(long Id, string Title, decimal SalePrice);

    private sealed record DealRow(long Id, string Title, string Stage);

    private sealed record StatusResponse(int ListingCount);

    private sealed record MeResponse(bool SignedIn, string Email, long UserId);

    /// <summary>One signed-in seller, with their own cookie jar.</summary>
    private sealed class Seller(HttpClient client) : IDisposable
    {
        public async Task LogSaleAsync(string title, decimal price)
        {
            var response = await client.PostAsJsonAsync("/api/earnings/flips",
                new { title, salePrice = price, soldUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero) });
            response.EnsureSuccessStatusCode();
        }

        public async Task TrackDealAsync(string title, string sourceItemId)
        {
            var response = await client.PostAsJsonAsync("/api/deals",
                new { title, source = "craigslist", sourceItemId, stage = DealStages.Sourced, askPrice = 4000m });
            response.EnsureSuccessStatusCode();
        }

        public async Task SaveListingAsync(string sku, string title)
        {
            var response = await client.PostAsJsonAsync("/api/local-listings/save-edit",
                new { sku, title, price = 1200m, quantity = 1 });
            response.EnsureSuccessStatusCode();
        }

        public async Task<List<FlipRow>> EarningsAsync() =>
            (await client.GetFromJsonAsync<List<FlipRow>>("/api/earnings"))!;

        public async Task<List<DealRow>> DealsAsync() =>
            (await client.GetFromJsonAsync<List<DealRow>>("/api/deals"))!;

        public async Task<int> ListingCountAsync() =>
            (await client.GetFromJsonAsync<StatusResponse>("/api/local-db/status"))!.ListingCount;

        public async Task<HttpStatusCode> DeleteSaleAsync(long id) =>
            (await client.DeleteAsync($"/api/earnings/flips/{id}")).StatusCode;

        public void Dispose() => client.Dispose();
    }

    private sealed class DataServer(WebApplication app, string root) : IAsyncDisposable
    {
        private readonly List<HttpClient> _clients = [];

        public async Task<Seller> SignUpAsync(string email)
        {
            var client   = NewClient();
            var response = await client.PostAsJsonAsync(HostedAuth.SignUpApi, new { email, password = Password, name = "Dana Ellis" });
            response.EnsureSuccessStatusCode();
            return new Seller(client);
        }

        public async Task<Seller> SignInAsync(string email)
        {
            var client   = NewClient();
            var response = await client.PostAsJsonAsync(HostedAuth.SignInApi, new { email, password = Password, name = "Dana Ellis" });
            response.EnsureSuccessStatusCode();
            return new Seller(client);
        }

        /// <summary>A separate cookie jar per client — which is what makes them different people.</summary>
        private HttpClient NewClient()
        {
            var client = new HttpClient(new HttpClientHandler
            {
                UseCookies        = true,
                CookieContainer   = new CookieContainer(),
                AllowAutoRedirect = false,
            })
            {
                BaseAddress = new Uri(app.Urls.First()),
            };
            _clients.Add(client);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var client in _clients) client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { /* a temp folder, not the point */ }
        }
    }
}
