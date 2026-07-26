using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Services;

// What the seller actually paid to own one unit of a listing.
//
// eBay never knows this — it is the one number in the whole profit calculation that has to come
// from the seller — and without it every "should I mark this down?" answer is a guess. With it,
// InventoryHealthAnalyzer can compute a real break-even floor and refuse to recommend a markdown
// that sells at a loss, which is the difference between a repricing tool and a discount button.
//
// Inbound shipping is stored separately from the unit cost rather than folded into it: sellers
// know what they paid for the item and what the freight was as two different numbers, and asking
// them to pre-add is how the number ends up wrong or blank.
public sealed class CostBasisEntry
{
    public string ListingId { get; set; } = "";
    public string Sku { get; set; } = "";
    public decimal UnitCost { get; set; }
    public decimal InboundShipping { get; set; }
    public string Note { get; set; } = "";
    public DateTimeOffset? AcquiredUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }

    // The all-in landed cost of one unit — what the item has to clear before a sale makes money.
    public decimal TotalUnitCost => Math.Round(UnitCost + InboundShipping, 2);
}

/// <summary>
/// Persists per-listing cost basis in the app's own SQLite database, keyed by eBay listing ID with
/// SKU as a secondary key.
/// </summary>
/// <remarks>
/// Both keys are carried because neither alone is reliable: listings created on the eBay website
/// often have no SKU, and a listing that is ended and relisted gets a new listing ID while keeping
/// its SKU. Writing one row and being able to find it either way means a relist doesn't silently
/// lose the cost the seller already entered.
/// </remarks>
public sealed class CostBasisStore
{
    private readonly string _databasePath;

    public CostBasisStore(ListingDatabase database) : this(database.DatabasePath) { }

    public CostBasisStore(string databasePath)
    {
        _databasePath = databasePath;
        Initialize();
    }

    public List<CostBasisEntry> GetAll()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT listing_id, sku, unit_cost, inbound_shipping, note, acquired_at, updated_at
            FROM listing_cost_basis;
            """;

        var entries = new List<CostBasisEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) entries.Add(Read(reader));
        return entries;
    }

    /// <summary>
    /// Looks up one listing's cost basis, preferring an exact listing-ID match and falling back to
    /// the SKU so a relisted item keeps its cost.
    /// </summary>
    public CostBasisEntry? Find(string? listingId, string? sku)
    {
        var all = GetAll();
        return Find(all, listingId, sku);
    }

    // Pure, and separated from the query so a whole scan can be resolved against one read of the
    // table instead of one SELECT per listing.
    public static CostBasisEntry? Find(IEnumerable<CostBasisEntry> entries, string? listingId, string? sku)
    {
        var list = entries as IList<CostBasisEntry> ?? entries.ToList();

        if (!string.IsNullOrWhiteSpace(listingId))
        {
            var byListing = list.FirstOrDefault(e => string.Equals(e.ListingId, listingId, StringComparison.OrdinalIgnoreCase));
            if (byListing is not null) return byListing;
        }

        if (!string.IsNullOrWhiteSpace(sku))
            return list.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Sku)
                                         && string.Equals(e.Sku, sku, StringComparison.OrdinalIgnoreCase));

        return null;
    }

    public CostBasisEntry Save(CostBasisEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.ListingId) && string.IsNullOrWhiteSpace(entry.Sku))
            throw new InvalidOperationException("A listing ID or SKU is required to record a cost basis.");
        if (entry.UnitCost < 0 || entry.InboundShipping < 0)
            throw new InvalidOperationException("Cost basis cannot be negative.");

        entry.UpdatedUtc = DateTimeOffset.UtcNow;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        // Delete-then-insert rather than UPSERT: a row can be matched on either key, so a save
        // that supplies both has to collapse a listing-keyed row and a SKU-keyed row into one
        // instead of colliding with the partial unique indexes.
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM listing_cost_basis
                WHERE (@listing_id <> '' AND listing_id = @listing_id)
                   OR (@sku <> '' AND sku = @sku);
                """;
            delete.Parameters.AddWithValue("@listing_id", entry.ListingId ?? "");
            delete.Parameters.AddWithValue("@sku", entry.Sku ?? "");
            delete.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO listing_cost_basis
                    (listing_id, sku, unit_cost, inbound_shipping, note, acquired_at, updated_at)
                VALUES
                    (@listing_id, @sku, @unit_cost, @inbound_shipping, @note, @acquired_at, @updated_at);
                """;
            insert.Parameters.AddWithValue("@listing_id", entry.ListingId ?? "");
            insert.Parameters.AddWithValue("@sku", entry.Sku ?? "");
            insert.Parameters.AddWithValue("@unit_cost", entry.UnitCost);
            insert.Parameters.AddWithValue("@inbound_shipping", entry.InboundShipping);
            insert.Parameters.AddWithValue("@note", entry.Note ?? "");
            insert.Parameters.AddWithValue("@acquired_at", entry.AcquiredUtc?.ToString("O") ?? "");
            insert.Parameters.AddWithValue("@updated_at", entry.UpdatedUtc.ToString("O"));
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
        return entry;
    }

    public int SaveMany(IEnumerable<CostBasisEntry> entries)
    {
        var saved = 0;
        foreach (var entry in entries) { Save(entry); saved++; }
        return saved;
    }

    public bool Delete(string? listingId, string? sku)
    {
        if (string.IsNullOrWhiteSpace(listingId) && string.IsNullOrWhiteSpace(sku)) return false;

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM listing_cost_basis
            WHERE (@listing_id <> '' AND listing_id = @listing_id)
               OR (@sku <> '' AND sku = @sku);
            """;
        command.Parameters.AddWithValue("@listing_id", listingId ?? "");
        command.Parameters.AddWithValue("@sku", sku ?? "");
        return command.ExecuteNonQuery() > 0;
    }

    private static CostBasisEntry Read(SqliteDataReader reader)
    {
        var acquired = reader.GetString(5);
        return new CostBasisEntry
        {
            ListingId = reader.GetString(0),
            Sku = reader.GetString(1),
            UnitCost = reader.GetDecimal(2),
            InboundShipping = reader.GetDecimal(3),
            Note = reader.GetString(4),
            AcquiredUtc = DateTimeOffset.TryParse(acquired, out var a) ? a : null,
            UpdatedUtc = DateTimeOffset.TryParse(reader.GetString(6), out var u) ? u : DateTimeOffset.MinValue,
        };
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS listing_cost_basis (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                listing_id TEXT NOT NULL DEFAULT '',
                sku TEXT NOT NULL DEFAULT '',
                unit_cost NUMERIC NOT NULL DEFAULT 0,
                inbound_shipping NUMERIC NOT NULL DEFAULT 0,
                note TEXT NOT NULL DEFAULT '',
                acquired_at TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL DEFAULT ''
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_cost_basis_listing_id
                ON listing_cost_basis(listing_id) WHERE listing_id <> '';

            CREATE UNIQUE INDEX IF NOT EXISTS ux_cost_basis_sku
                ON listing_cost_basis(sku) WHERE sku <> '';
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        connection.Open();
        return connection;
    }
}
