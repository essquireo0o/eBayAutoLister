using ING_eBay_AutoLister.Models;
using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Persists completed flips — imported eBay order lines and flips the seller logged by hand — in
/// the app's own SQLite database, alongside <see cref="CostBasisStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// The only hard requirement on this table is that importing an overlapping date range twice must
/// not double the seller's earnings. eBay order lines are therefore keyed on
/// (user_id, order_id, line_item_id) with a unique index, and an import updates in place. Manual
/// flips have no such key and are identified only by their row id, which is why they are never
/// touched by an import.
/// </para>
/// <para>
/// Every row belongs to a user and every statement here filters on the one making the request. See
/// <see cref="UserScope"/>: on a shared deployment this table is the seller's revenue, their costs
/// and their margins, and it is the first thing a second person signing up must not be shown.
/// </para>
/// </remarks>
public sealed class EarningsStore
{
    private readonly string _databasePath;
    private readonly UserScope _scope;

    public EarningsStore(ListingDatabase database, UserScope? scope = null) : this(database.DatabasePath, scope) { }

    public EarningsStore(string databasePath, UserScope? scope = null)
    {
        _databasePath = databasePath;
        _scope        = scope ?? UserScope.Desktop;
        Initialize();
    }

    public List<FlipRecord> GetAll()
    {
        if (_scope.OwnerId is not { } owner) return [];

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, source, order_id, line_item_id, listing_id, sku, title, sold_at, quantity,
                   sale_price, shipping_charged, marketplace_fee, shipping_cost, other_costs,
                   unit_cost, refunded_amount, status, note, updated_at, cost_confirmed_at
            FROM flips
            WHERE user_id = @user_id
            ORDER BY sold_at DESC, id DESC;
            """;
        command.Parameters.AddWithValue("@user_id", owner);

        var rows = new List<FlipRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(Read(reader));
        return rows;
    }

    public FlipRecord? Get(long id) => GetAll().FirstOrDefault(f => f.Id == id);

    /// <summary>
    /// One eBay order line, read fresh. Used by the importer so that what it carries forward onto a
    /// re-imported row is what is on that row NOW, not what was there before the import started
    /// talking to eBay — the seller may have typed a cost in the meantime, and it would be written
    /// straight back out. Hits the unique (order_id, line_item_id) index.
    /// </summary>
    public FlipRecord? FindByOrderLine(string? orderId, string? lineItemId)
    {
        if (string.IsNullOrWhiteSpace(orderId) || string.IsNullOrWhiteSpace(lineItemId)) return null;
        if (_scope.OwnerId is not { } owner) return null;

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, source, order_id, line_item_id, listing_id, sku, title, sold_at, quantity,
                   sale_price, shipping_charged, marketplace_fee, shipping_cost, other_costs,
                   unit_cost, refunded_amount, status, note, updated_at, cost_confirmed_at
            FROM flips
            WHERE user_id = @user_id AND order_id = @order_id AND line_item_id = @line_item_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@user_id", owner);
        command.Parameters.AddWithValue("@order_id", orderId);
        command.Parameters.AddWithValue("@line_item_id", lineItemId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>
    /// Inserts or updates one flip. eBay lines match on (order_id, line_item_id); everything else
    /// matches on the row id.
    /// </summary>
    /// <returns>True when the row was newly inserted.</returns>
    public bool Upsert(FlipRecord flip)
    {
        Validate(flip);
        // Nobody signed in on a hosted deployment: the sale is not written rather than written into
        // whichever seller's earnings happened to be last. See UserScope.
        if (_scope.OwnerId is not { } owner) return false;

        flip.UpdatedUtc = DateTimeOffset.UtcNow;

        using var connection = OpenConnection();
        var existingId = FindExistingId(connection, flip, owner);

        if (existingId is null)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO flips
                    (user_id, source, order_id, line_item_id, listing_id, sku, title, sold_at, quantity,
                     sale_price, shipping_charged, marketplace_fee, shipping_cost, other_costs,
                     unit_cost, refunded_amount, status, note, updated_at, cost_confirmed_at)
                VALUES
                    (@user_id, @source, @order_id, @line_item_id, @listing_id, @sku, @title, @sold_at, @quantity,
                     @sale_price, @shipping_charged, @marketplace_fee, @shipping_cost, @other_costs,
                     @unit_cost, @refunded_amount, @status, @note, @updated_at, @cost_confirmed_at);
                SELECT last_insert_rowid();
                """;
            Bind(insert, flip);
            insert.Parameters.AddWithValue("@user_id", owner);
            flip.Id = Convert.ToInt64(insert.ExecuteScalar());
            return true;
        }

        flip.Id = existingId.Value;
        using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE flips SET
                source = @source, order_id = @order_id, line_item_id = @line_item_id,
                listing_id = @listing_id, sku = @sku, title = @title, sold_at = @sold_at,
                quantity = @quantity, sale_price = @sale_price, shipping_charged = @shipping_charged,
                marketplace_fee = @marketplace_fee, shipping_cost = @shipping_cost,
                other_costs = @other_costs, unit_cost = @unit_cost, refunded_amount = @refunded_amount,
                status = @status, note = @note, updated_at = @updated_at,
                cost_confirmed_at = @cost_confirmed_at
            WHERE id = @id AND user_id = @user_id;
            """;
        Bind(update, flip);
        update.Parameters.AddWithValue("@id", flip.Id);
        // The owner is on the UPDATE as well as on the lookup that found the row. A caller that
        // supplies a row id it does not own — a hand-made request, a stale page — changes nothing
        // rather than editing somebody else's sale.
        update.Parameters.AddWithValue("@user_id", owner);
        update.ExecuteNonQuery();
        return false;
    }

    /// <summary>
    /// Applies an edit from the browser to an existing flip, changing only the fields that were
    /// actually supplied.
    /// </summary>
    /// <remarks>
    /// Field-level rather than whole-object, because the earnings screen edits one cell at a time
    /// (usually just the cost) and a whole-object PUT from a form that never loaded the eBay order
    /// id would blank the import key and re-duplicate the row on the next import.
    /// </remarks>
    public FlipRecord? ApplyEdit(long id, FlipUpsertRequest edit)
    {
        var flip = Get(id);
        if (flip is null) return null;

        if (edit.Title is not null) flip.Title = edit.Title.Trim();
        if (edit.ListingId is not null) flip.ListingId = edit.ListingId.Trim();
        if (edit.Sku is not null) flip.Sku = edit.Sku.Trim();
        if (edit.SoldUtc.HasValue) flip.SoldUtc = edit.SoldUtc.Value;
        if (edit.Quantity.HasValue) flip.Quantity = Math.Max(1, edit.Quantity.Value);
        if (edit.SalePrice.HasValue) flip.SalePrice = edit.SalePrice.Value;
        if (edit.ShippingCharged.HasValue) flip.ShippingCharged = edit.ShippingCharged.Value;
        if (edit.MarketplaceFee.HasValue) flip.MarketplaceFee = edit.MarketplaceFee.Value;
        if (edit.ShippingCost.HasValue) flip.ShippingCost = edit.ShippingCost.Value;
        if (edit.OtherCosts.HasValue) flip.OtherCosts = edit.OtherCosts.Value;
        // A cost typed here is the seller answering too, zero included — the same answer the
        // awaiting-cost panel takes, so it has to stop the asking the same way.
        if (edit.UnitCost.HasValue) { flip.UnitCost = edit.UnitCost.Value; flip.CostConfirmedUtc = DateTimeOffset.UtcNow; }
        if (edit.RefundedAmount.HasValue) flip.RefundedAmount = edit.RefundedAmount.Value;
        if (!string.IsNullOrWhiteSpace(edit.Status)) flip.Status = NormalizeStatus(edit.Status);
        if (edit.Note is not null) flip.Note = edit.Note.Trim();

        Upsert(flip);
        return flip;
    }

    public bool Delete(long id)
    {
        if (_scope.OwnerId is not { } owner) return false;

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM flips WHERE id = @id AND user_id = @user_id;";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@user_id", owner);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>Builds a validated record from a browser-supplied manual flip.</summary>
    public static FlipRecord FromRequest(FlipUpsertRequest req)
    {
        var flip = new FlipRecord
        {
            Source = "manual",
            Title = (req.Title ?? "").Trim(),
            ListingId = (req.ListingId ?? "").Trim(),
            Sku = (req.Sku ?? "").Trim(),
            SoldUtc = req.SoldUtc ?? DateTimeOffset.UtcNow,
            Quantity = Math.Max(1, req.Quantity ?? 1),
            SalePrice = req.SalePrice ?? 0m,
            ShippingCharged = req.ShippingCharged ?? 0m,
            MarketplaceFee = req.MarketplaceFee,
            ShippingCost = req.ShippingCost,
            OtherCosts = req.OtherCosts ?? 0m,
            UnitCost = req.UnitCost,
            RefundedAmount = req.RefundedAmount ?? 0m,
            Status = NormalizeStatus(req.Status),
            Note = (req.Note ?? "").Trim(),
        };
        Validate(flip);
        return flip;
    }

    public static string NormalizeStatus(string? status) => (status ?? "").Trim().ToLowerInvariant() switch
    {
        "refunded" => "refunded",
        "cancelled" or "canceled" => "cancelled",
        _ => "paid",
    };

    // Rejects the inputs that would quietly corrupt a running total rather than fail loudly. A
    // negative sale price or a blank title is a typo; letting either through means the seller finds
    // out when their all-time figure moves the wrong way and stops believing any of it.
    private static void Validate(FlipRecord flip)
    {
        if (string.IsNullOrWhiteSpace(flip.Title))
            throw new InvalidOperationException("A sale needs an item name.");
        if (flip.SalePrice < 0 || flip.ShippingCharged < 0 || flip.OtherCosts < 0 || flip.RefundedAmount < 0)
            throw new InvalidOperationException("Sale amounts can't be negative.");
        if (flip.UnitCost is < 0) throw new InvalidOperationException("Cost can't be negative.");
        if (flip.ShippingCost is < 0) throw new InvalidOperationException("Shipping cost can't be negative.");
        if (flip.MarketplaceFee is < 0) throw new InvalidOperationException("Fees can't be negative.");
        if (flip.Quantity < 1) throw new InvalidOperationException("Quantity has to be at least 1.");
        // A refund larger than the sale would read as negative revenue and drag the all-time total
        // below what the seller actually lost.
        if (flip.RefundedAmount > flip.SalePrice + flip.ShippingCharged)
            throw new InvalidOperationException("A refund can't be larger than what the buyer paid.");
        if (flip.SoldUtc > DateTimeOffset.UtcNow.AddDays(2))
            throw new InvalidOperationException("That sale date is in the future.");
    }

    private static long? FindExistingId(SqliteConnection connection, FlipRecord flip, long owner)
    {
        if (flip.Id > 0) return flip.Id;
        if (string.IsNullOrWhiteSpace(flip.OrderId) || string.IsNullOrWhiteSpace(flip.LineItemId)) return null;

        using var command = connection.CreateCommand();
        // Within this user's rows only. Two sellers can hold the same eBay order id — one seller
        // with two accounts is enough — and matching across users would make one person's import
        // silently overwrite the other's sale.
        command.CommandText = """
            SELECT id FROM flips
            WHERE user_id = @user_id AND order_id = @order_id AND line_item_id = @line_item_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@user_id", owner);
        command.Parameters.AddWithValue("@order_id", flip.OrderId);
        command.Parameters.AddWithValue("@line_item_id", flip.LineItemId);
        var found = command.ExecuteScalar();
        return found is null or DBNull ? null : Convert.ToInt64(found);
    }

    private static void Bind(SqliteCommand command, FlipRecord flip)
    {
        command.Parameters.AddWithValue("@source", flip.Source ?? "manual");
        command.Parameters.AddWithValue("@order_id", flip.OrderId ?? "");
        command.Parameters.AddWithValue("@line_item_id", flip.LineItemId ?? "");
        command.Parameters.AddWithValue("@listing_id", flip.ListingId ?? "");
        command.Parameters.AddWithValue("@sku", flip.Sku ?? "");
        command.Parameters.AddWithValue("@title", flip.Title ?? "");
        command.Parameters.AddWithValue("@sold_at", flip.SoldUtc.ToString("O"));
        command.Parameters.AddWithValue("@quantity", flip.Quantity);
        command.Parameters.AddWithValue("@sale_price", flip.SalePrice);
        command.Parameters.AddWithValue("@shipping_charged", flip.ShippingCharged);
        command.Parameters.AddWithValue("@marketplace_fee", (object?)flip.MarketplaceFee ?? DBNull.Value);
        command.Parameters.AddWithValue("@shipping_cost", (object?)flip.ShippingCost ?? DBNull.Value);
        command.Parameters.AddWithValue("@other_costs", flip.OtherCosts);
        command.Parameters.AddWithValue("@unit_cost", (object?)flip.UnitCost ?? DBNull.Value);
        command.Parameters.AddWithValue("@refunded_amount", flip.RefundedAmount);
        command.Parameters.AddWithValue("@status", flip.Status ?? "paid");
        command.Parameters.AddWithValue("@note", flip.Note ?? "");
        command.Parameters.AddWithValue("@updated_at", flip.UpdatedUtc.ToString("O"));
        // The seller's own answer about what this cost, as opposed to a zero that arrived from an
        // eBay import. Null until they say. Without this column the confirmation lived only on the
        // in-memory object the request happened to be holding, so "save $0.00 to stop being asked"
        // did nothing at all: the next read rebuilt the row from the database with no memory of it,
        // and the sale came straight back to the awaiting-cost list.
        command.Parameters.AddWithValue("@cost_confirmed_at",
            (object?)flip.CostConfirmedUtc?.ToString("O") ?? DBNull.Value);
    }

    private static FlipRecord Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Source = reader.GetString(1),
        OrderId = reader.GetString(2),
        LineItemId = reader.GetString(3),
        ListingId = reader.GetString(4),
        Sku = reader.GetString(5),
        Title = reader.GetString(6),
        SoldUtc = DateTimeOffset.TryParse(reader.GetString(7), out var sold) ? sold : DateTimeOffset.MinValue,
        Quantity = reader.GetInt32(8),
        SalePrice = reader.GetDecimal(9),
        ShippingCharged = reader.GetDecimal(10),
        MarketplaceFee = reader.IsDBNull(11) ? null : reader.GetDecimal(11),
        ShippingCost = reader.IsDBNull(12) ? null : reader.GetDecimal(12),
        OtherCosts = reader.GetDecimal(13),
        UnitCost = reader.IsDBNull(14) ? null : reader.GetDecimal(14),
        RefundedAmount = reader.GetDecimal(15),
        Status = reader.GetString(16),
        Note = reader.GetString(17),
        UpdatedUtc = DateTimeOffset.TryParse(reader.GetString(18), out var updated) ? updated : DateTimeOffset.MinValue,
        CostConfirmedUtc = reader.IsDBNull(19) ? null
            : DateTimeOffset.TryParse(reader.GetString(19), out var confirmed) ? confirmed : null,
    };

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS flips (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                -- Whose sale this is. 0 on a desktop database, where there is only the one seller.
                user_id INTEGER NOT NULL DEFAULT 0,
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
                updated_at TEXT NOT NULL DEFAULT '',
                -- When the SELLER said what this cost, as opposed to a cost that arrived with an
                -- import. Only this column can tell "free, and I'm sure" from "$0.00 because eBay
                -- had nothing to send" — a bare 0 in unit_cost reads identically for both.
                cost_confirmed_at TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_flips_sold_at ON flips(sold_at);
            """;
        command.ExecuteNonQuery();

        // Every database created before the confirmation existed still has the original schema and
        // will carry it for as long as it runs. Added in place rather than rebuilt: NULL is exactly
        // what those rows know — nobody has confirmed any of their costs yet.
        UserOwnedTable.AddColumnIfMissing(connection, "flips", "cost_confirmed_at", "TEXT NULL");

        // The owner column, before the index that uses it — on an old database it does not exist yet.
        UserOwnedTable.Migrate(connection, "flips");

        using var indexes = connection.CreateCommand();
        indexes.CommandText = """
            -- The one index that matters: it is what makes a re-import an update instead of a
            -- second copy of the same money. Per user, because the same eBay order id can legitimately
            -- appear for two of them, and a shared index would refuse the second seller's import
            -- outright — a unique-constraint failure on a screen that just says "importing".
            DROP INDEX IF EXISTS ux_flips_order_line;

            CREATE UNIQUE INDEX IF NOT EXISTS ux_flips_user_order_line
                ON flips(user_id, order_id, line_item_id) WHERE order_id <> '' AND line_item_id <> '';
            """;
        indexes.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        connection.Open();
        return connection;
    }
}
