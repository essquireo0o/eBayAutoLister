using Microsoft.Data.Sqlite;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>One Facebook Marketplace tile that carried a Sold or Pending badge.</summary>
/// <remarks>
/// Read the property name <see cref="LastAskPrice"/> literally. It is not a sale price and there is
/// no field here for one, because Facebook does not publish one.
/// </remarks>
public class FacebookSoldRow
{
    public string ItemId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>The price shown on the tile when it was seen marked sold — an ASK, never a sale.</summary>
    public decimal? LastAskPrice { get; set; }
    public string Location { get; set; } = "";
    public double? DistanceMiles { get; set; }
    /// <summary>The badge as Facebook worded it: "Sold", "Pending", "Sale pending".</summary>
    public string SoldState { get; set; } = "";
    /// <summary>The search that happened to surface it — this is a by-product of scans, not a sweep.</summary>
    public string SeenForQuery { get; set; } = "";
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
}

/// <summary>
/// Facebook Marketplace items seen marked Sold or Pending, kept in the app's own database.
///
/// WHAT THIS IS NOT: a source of sold comps. Facebook publishes no sale prices — there is no
/// completed-listings search, and a tile marked Sold still shows the seller's last ASKING price.
/// A $450 ask may have changed hands for $300 and Marketplace will never say. So nothing here is
/// evidence of what anything sold FOR, and this table is deliberately not reachable from
/// <see cref="IMarketplaceRepository"/>, the comp matcher, the price estimator or the profit
/// calculator. Those are fed by <c>SoldListings</c>, which holds real eBay sale prices and which
/// this app opens read-only precisely so nothing can slip into it.
///
/// Putting asks into that table would not have failed loudly. Every estimate would simply have
/// shifted, silently, toward numbers nobody paid.
///
/// WHAT IT IS FOR: the only demand signal Marketplace gives away — that a thing moved locally, at
/// roughly what asking price, and how fast. Useful for "does this sell around here", worthless for
/// "what is it worth". Anything showing these rows has to label them as asks.
///
/// Filled as a by-product of searches the seller already runs, so it costs no extra Marketplace
/// traffic against their logged-in account and cannot get them checkpointed on its own.
/// </summary>
public class FacebookSoldStore
{
    private readonly string _connectionString;
    private readonly object _sync = new();

    public FacebookSoldStore(ListingDatabase db)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = db.DatabasePath }.ToString();
        Initialize();
    }

    /// <summary>
    /// Records sold/pending rows from one scan. Re-seeing an item updates when it was last seen and
    /// its ask, and keeps the first sighting — a tile that sat marked Pending for a week and then
    /// went is a different story from one that appeared and vanished the same day.
    /// </summary>
    /// <returns>How many rows were new.</returns>
    public int Record(IEnumerable<LocalSupplyListing> soldItems, string query)
    {
        var rows = (soldItems ?? []).Where(i => i is { IsSold: true, ItemId.Length: > 0 }).ToList();
        if (rows.Count == 0) return 0;

        var now = DateTimeOffset.UtcNow;
        var added = 0;

        lock (_sync)
        {
            using var connection = OpenConnection();
            using var tx = connection.BeginTransaction();

            foreach (var item in rows)
            {
                using var command = connection.CreateCommand();
                command.Transaction = tx;
                command.CommandText = """
                    INSERT INTO facebook_sold
                        (item_id, title, url, last_ask_price, location, distance_miles,
                         sold_state, seen_for_query, first_seen_utc, last_seen_utc)
                    VALUES
                        ($id, $title, $url, $price, $location, $distance,
                         $state, $query, $now, $now)
                    ON CONFLICT(item_id) DO UPDATE SET
                        last_ask_price = excluded.last_ask_price,
                        sold_state     = excluded.sold_state,
                        last_seen_utc  = excluded.last_seen_utc;
                    """;
                command.Parameters.AddWithValue("$id", item.ItemId);
                command.Parameters.AddWithValue("$title", item.Title ?? "");
                command.Parameters.AddWithValue("$url", item.Url ?? "");
                command.Parameters.AddWithValue("$price", (object?)item.Price ?? DBNull.Value);
                command.Parameters.AddWithValue("$location", item.Location ?? "");
                command.Parameters.AddWithValue("$distance", (object?)item.DistanceMiles ?? DBNull.Value);
                command.Parameters.AddWithValue("$state", string.IsNullOrWhiteSpace(item.SoldStateText) ? "Sold" : item.SoldStateText);
                command.Parameters.AddWithValue("$query", query ?? "");
                command.Parameters.AddWithValue("$now", now.ToString("O"));
                added += command.ExecuteNonQuery() > 0 && !Exists(connection, tx, item.ItemId, now) ? 1 : 0;
            }

            tx.Commit();
        }

        return added;
    }

    // "New" means the row's first sighting is this run. Cheaper and more honest than trusting a
    // changes-count, which an upsert reports as 1 either way.
    private static bool Exists(SqliteConnection connection, SqliteTransaction tx, string itemId, DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT first_seen_utc FROM facebook_sold WHERE item_id = $id;";
        command.Parameters.AddWithValue("$id", itemId);
        var first = command.ExecuteScalar() as string;
        return first is not null && first != now.ToString("O");
    }

    /// <summary>Most recently seen first. <paramref name="query"/> filters on the search that found them.</summary>
    public List<FacebookSoldRow> Recent(string? query = null, int limit = 100)
    {
        var rows = new List<FacebookSoldRow>();
        lock (_sync)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = string.IsNullOrWhiteSpace(query)
                ? "SELECT * FROM facebook_sold ORDER BY last_seen_utc DESC LIMIT $limit;"
                : "SELECT * FROM facebook_sold WHERE seen_for_query LIKE $q ORDER BY last_seen_utc DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
            if (!string.IsNullOrWhiteSpace(query)) command.Parameters.AddWithValue("$q", $"%{query.Trim()}%");

            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add(Read(reader));
        }
        return rows;
    }

    public int Count()
    {
        lock (_sync)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM facebook_sold;";
            return Convert.ToInt32(command.ExecuteScalar() ?? 0);
        }
    }

    private static FacebookSoldRow Read(SqliteDataReader r) => new()
    {
        ItemId        = r["item_id"] as string ?? "",
        Title         = r["title"] as string ?? "",
        Url           = r["url"] as string ?? "",
        LastAskPrice  = r["last_ask_price"] is DBNull ? null : Convert.ToDecimal(r["last_ask_price"]),
        Location      = r["location"] as string ?? "",
        DistanceMiles = r["distance_miles"] is DBNull ? null : Convert.ToDouble(r["distance_miles"]),
        SoldState     = r["sold_state"] as string ?? "",
        SeenForQuery  = r["seen_for_query"] as string ?? "",
        FirstSeenUtc  = DateTimeOffset.TryParse(r["first_seen_utc"] as string, out var f) ? f : default,
        LastSeenUtc   = DateTimeOffset.TryParse(r["last_seen_utc"] as string, out var l) ? l : default,
    };

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        // Column named last_ask_price, not price or sold_price, so that a future query written
        // against this table cannot read it as a sale figure by accident.
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS facebook_sold (
                item_id         TEXT PRIMARY KEY,
                title           TEXT NOT NULL,
                url             TEXT NOT NULL,
                last_ask_price  REAL NULL,
                location        TEXT NOT NULL DEFAULT '',
                distance_miles  REAL NULL,
                sold_state      TEXT NOT NULL DEFAULT 'Sold',
                seen_for_query  TEXT NOT NULL DEFAULT '',
                first_seen_utc  TEXT NOT NULL,
                last_seen_utc   TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_facebook_sold_last_seen ON facebook_sold(last_seen_utc);
            CREATE INDEX IF NOT EXISTS ix_facebook_sold_query     ON facebook_sold(seen_for_query);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
