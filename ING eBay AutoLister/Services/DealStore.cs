using ING_eBay_AutoLister.Models;
using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Persists tracked deals — the Sourced → Bought → Listed → Sold pipeline — in the app's own
/// SQLite database, alongside <see cref="CostBasisStore"/> and <see cref="EarningsStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// The one hard requirement here is that pressing "Track" twice on the same local listing must not
/// produce two deals: a duplicated card duplicates its capital and its projected profit in every
/// roll-up on the board. Source-tracked deals are therefore keyed on
/// (user_id, source, source_item_id) with a unique index, and a second track updates the existing
/// row. Deals typed in by hand have no such key and are identified only by their row id.
/// </para>
/// <para>
/// The user is in that key, not merely on the row. Two sellers looking at the same city's
/// Craigslist see the same posts, and a shared key would make the second one to press Track
/// silently overwrite the first one's card — their purchase price, their frozen projection, their
/// stage. Every read here filters on the seller asking; see <see cref="UserScope"/>.
/// </para>
/// </remarks>
public sealed class DealStore
{
    private readonly string _databasePath;
    private readonly UserScope _scope;

    public DealStore(ListingDatabase database, UserScope? scope = null) : this(database.DatabasePath, scope) { }

    public DealStore(string databasePath, UserScope? scope = null)
    {
        _databasePath = databasePath;
        _scope        = scope ?? UserScope.Desktop;
        Initialize();
    }

    public List<DealRecord> GetAll()
    {
        if (_scope.OwnerId is not { } owner) return [];

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, stage, title, source, source_label, source_url, source_item_id, quantity,
                   ask_price, max_buy_price, projected_sale_price, projected_net_profit,
                   projected_roi_percent, projected_days_to_cash, projected_basis, projected_at,
                   purchase_price, purchase_extra_cost, bought_at,
                   listing_id, sku, listed_price, listed_at,
                   sold_at, note, created_at, updated_at, stage_changed_at
            FROM deals
            WHERE user_id = @user_id
            ORDER BY id DESC;
            """;
        command.Parameters.AddWithValue("@user_id", owner);

        var rows = new List<DealRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(Read(reader));
        return rows;
    }

    public DealRecord? Get(long id) => GetAll().FirstOrDefault(d => d.Id == id);

    /// <summary>Inserts or updates one deal. Source-tracked deals match on (source, source item id).</summary>
    /// <returns>True when the deal was newly inserted.</returns>
    public bool Upsert(DealRecord deal)
    {
        Validate(deal);
        // Nobody signed in on a hosted deployment: the card is not written rather than written onto
        // somebody's board. See UserScope.
        if (_scope.OwnerId is not { } owner) return false;

        var now = DateTimeOffset.UtcNow;
        deal.UpdatedUtc = now;
        if (deal.CreatedUtc == default) deal.CreatedUtc = now;
        if (deal.StageChangedUtc == default) deal.StageChangedUtc = now;

        using var connection = OpenConnection();
        var existingId = FindExistingId(connection, deal, owner);

        if (existingId is null)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO deals
                    (user_id, stage, title, source, source_label, source_url, source_item_id, quantity,
                     ask_price, max_buy_price, projected_sale_price, projected_net_profit,
                     projected_roi_percent, projected_days_to_cash, projected_basis, projected_at,
                     purchase_price, purchase_extra_cost, bought_at,
                     listing_id, sku, listed_price, listed_at,
                     sold_at, note, created_at, updated_at, stage_changed_at)
                VALUES
                    (@user_id, @stage, @title, @source, @source_label, @source_url, @source_item_id, @quantity,
                     @ask_price, @max_buy_price, @projected_sale_price, @projected_net_profit,
                     @projected_roi_percent, @projected_days_to_cash, @projected_basis, @projected_at,
                     @purchase_price, @purchase_extra_cost, @bought_at,
                     @listing_id, @sku, @listed_price, @listed_at,
                     @sold_at, @note, @created_at, @updated_at, @stage_changed_at);
                SELECT last_insert_rowid();
                """;
            Bind(insert, deal);
            insert.Parameters.AddWithValue("@user_id", owner);
            deal.Id = Convert.ToInt64(insert.ExecuteScalar());
            return true;
        }

        deal.Id = existingId.Value;
        using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE deals SET
                stage = @stage, title = @title, source = @source, source_label = @source_label,
                source_url = @source_url, source_item_id = @source_item_id, quantity = @quantity,
                ask_price = @ask_price, max_buy_price = @max_buy_price,
                projected_sale_price = @projected_sale_price, projected_net_profit = @projected_net_profit,
                projected_roi_percent = @projected_roi_percent, projected_days_to_cash = @projected_days_to_cash,
                projected_basis = @projected_basis, projected_at = @projected_at,
                purchase_price = @purchase_price, purchase_extra_cost = @purchase_extra_cost,
                bought_at = @bought_at, listing_id = @listing_id, sku = @sku,
                listed_price = @listed_price, listed_at = @listed_at, sold_at = @sold_at,
                note = @note, created_at = @created_at, updated_at = @updated_at,
                stage_changed_at = @stage_changed_at
            WHERE id = @id AND user_id = @user_id;
            """;
        Bind(update, deal);
        update.Parameters.AddWithValue("@id", deal.Id);
        // On the UPDATE as well as on the lookup: a row id this seller does not own edits nothing.
        update.Parameters.AddWithValue("@user_id", owner);
        update.ExecuteNonQuery();
        return false;
    }

    /// <summary>
    /// Applies a browser edit to an existing deal, changing only the fields actually supplied.
    /// </summary>
    /// <remarks>
    /// Field-level rather than whole-object for the same reason <see cref="EarningsStore.ApplyEdit"/>
    /// is: the board edits one thing at a time (a purchase price, a listing ID), and a whole-object
    /// write from a form that never loaded the frozen projection would blank the forecast — which is
    /// the one field on a deal that cannot be reconstructed after the fact.
    /// </remarks>
    public DealRecord? ApplyEdit(long id, DealUpsertRequest edit)
    {
        var deal = Get(id);
        if (deal is null) return null;

        if (edit.Title is not null) deal.Title = edit.Title.Trim();
        if (edit.SourceUrl is not null) deal.SourceUrl = edit.SourceUrl.Trim();
        if (edit.Quantity.HasValue) deal.Quantity = Math.Max(1, edit.Quantity.Value);

        if (edit.AskPrice.HasValue) deal.AskPrice = edit.AskPrice.Value;
        if (edit.MaxBuyPrice.HasValue) deal.MaxBuyPrice = edit.MaxBuyPrice.Value;
        if (edit.ProjectedSalePrice.HasValue) deal.ProjectedSalePrice = edit.ProjectedSalePrice.Value;
        if (edit.ProjectedNetProfit.HasValue) deal.ProjectedNetProfit = edit.ProjectedNetProfit.Value;
        if (edit.ProjectedRoiPercent.HasValue) deal.ProjectedRoiPercent = edit.ProjectedRoiPercent.Value;
        if (edit.ProjectedDaysToCash.HasValue) deal.ProjectedDaysToCash = edit.ProjectedDaysToCash.Value;

        if (edit.PurchasePrice.HasValue) deal.PurchasePrice = edit.PurchasePrice.Value;
        if (edit.PurchaseExtraCost.HasValue) deal.PurchaseExtraCost = edit.PurchaseExtraCost.Value;
        if (edit.BoughtUtc.HasValue) deal.BoughtUtc = edit.BoughtUtc.Value;

        if (edit.ListingId is not null) deal.ListingId = edit.ListingId.Trim();
        if (edit.Sku is not null) deal.Sku = edit.Sku.Trim();
        if (edit.ListedPrice.HasValue) deal.ListedPrice = edit.ListedPrice.Value;
        if (edit.ListedUtc.HasValue) deal.ListedUtc = edit.ListedUtc.Value;

        if (edit.SoldUtc.HasValue) deal.SoldUtc = edit.SoldUtc.Value;
        if (edit.Note is not null) deal.Note = edit.Note.Trim();

        if (!string.IsNullOrWhiteSpace(edit.Stage))
        {
            var stage = DealStages.Normalize(edit.Stage);
            if (stage != deal.Stage) deal.StageChangedUtc = DateTimeOffset.UtcNow;
            deal.Stage = stage;
        }

        StampStageDates(deal);
        Upsert(deal);
        return deal;
    }

    public bool Delete(long id)
    {
        if (_scope.OwnerId is not { } owner) return false;

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM deals WHERE id = @id AND user_id = @user_id;";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@user_id", owner);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>Builds a validated deal from a browser request — the "Track this deal" payload.</summary>
    public static DealRecord FromRequest(DealUpsertRequest req)
    {
        var deal = new DealRecord
        {
            Stage = DealStages.Normalize(req.Stage),
            Title = (req.Title ?? "").Trim(),
            Source = string.IsNullOrWhiteSpace(req.Source) ? "manual" : req.Source.Trim().ToLowerInvariant(),
            SourceLabel = (req.SourceLabel ?? "").Trim(),
            SourceUrl = (req.SourceUrl ?? "").Trim(),
            SourceItemId = (req.SourceItemId ?? "").Trim(),
            Quantity = Math.Max(1, req.Quantity ?? 1),
            AskPrice = req.AskPrice,
            MaxBuyPrice = req.MaxBuyPrice,
            ProjectedSalePrice = req.ProjectedSalePrice,
            ProjectedNetProfit = req.ProjectedNetProfit,
            ProjectedRoiPercent = req.ProjectedRoiPercent,
            ProjectedDaysToCash = req.ProjectedDaysToCash,
            ProjectedBasis = (req.ProjectedBasis ?? "").Trim(),
            PurchasePrice = req.PurchasePrice,
            PurchaseExtraCost = req.PurchaseExtraCost ?? 0m,
            BoughtUtc = req.BoughtUtc,
            ListingId = (req.ListingId ?? "").Trim(),
            Sku = (req.Sku ?? "").Trim(),
            ListedPrice = req.ListedPrice,
            ListedUtc = req.ListedUtc,
            SoldUtc = req.SoldUtc,
            Note = (req.Note ?? "").Trim(),
        };

        // A forecast is only worth grading if it can be dated. Stamping it here rather than
        // trusting the browser means a projection can never claim to be older than the deal.
        if (deal.ProjectedNetProfit.HasValue || deal.ProjectedSalePrice.HasValue)
            deal.ProjectedUtc = DateTimeOffset.UtcNow;

        StampStageDates(deal);
        Validate(deal);
        return deal;
    }

    /// <summary>
    /// Fills in the date a stage was reached when the caller didn't supply one, so "19 days in
    /// Bought" can be measured on a deal that was dragged straight from Sourced to Listed.
    /// </summary>
    private static void StampStageDates(DealRecord deal)
    {
        var now = DateTimeOffset.UtcNow;
        var reached = DealStages.Order(deal.Stage);

        if (reached >= DealStages.Order(DealStages.Bought) && deal.Stage != DealStages.Dropped)
            deal.BoughtUtc ??= now;
        if (reached >= DealStages.Order(DealStages.Listed) && deal.Stage != DealStages.Dropped)
            deal.ListedUtc ??= now;
        if (deal.Stage == DealStages.Sold)
            deal.SoldUtc ??= now;
    }

    // Rejects what would corrupt a board total rather than let it through and be discovered as a
    // wrong capital figure later. Deliberately narrow: a deal is a working note, and refusing to
    // save one because a field is blank is how a seller stops using the pipeline.
    private static void Validate(DealRecord deal)
    {
        if (string.IsNullOrWhiteSpace(deal.Title))
            throw new InvalidOperationException("A deal needs an item name.");
        if (deal.Quantity < 1)
            throw new InvalidOperationException("Quantity has to be at least 1.");
        if (deal.PurchasePrice is < 0)
            throw new InvalidOperationException("What you paid can't be negative.");
        if (deal.PurchaseExtraCost < 0)
            throw new InvalidOperationException("Extra costs can't be negative.");
        if (deal.AskPrice is < 0 || deal.ListedPrice is < 0 || deal.ProjectedSalePrice is < 0)
            throw new InvalidOperationException("Prices can't be negative.");
        // A deal in Bought with no price paid reports zero capital at risk, which is the one number
        // on this board that must never be understated.
        if ((deal.Stage == DealStages.Bought || deal.Stage == DealStages.Listed) && deal.PurchasePrice is null)
            throw new InvalidOperationException("Record what you paid — that's the number that tells you what's at risk.");
        if (deal.BoughtUtc > DateTimeOffset.UtcNow.AddDays(2) || deal.SoldUtc > DateTimeOffset.UtcNow.AddDays(2))
            throw new InvalidOperationException("That date is in the future.");
    }

    private static long? FindExistingId(SqliteConnection connection, DealRecord deal, long owner)
    {
        if (deal.Id > 0) return deal.Id;
        if (string.IsNullOrWhiteSpace(deal.Source) || string.IsNullOrWhiteSpace(deal.SourceItemId)) return null;

        using var command = connection.CreateCommand();
        // This seller's board only: the same post tracked by two of them is two deals, because it
        // is two people deciding separately whether to buy it.
        command.CommandText = """
            SELECT id FROM deals
            WHERE user_id = @user_id AND source = @source AND source_item_id = @source_item_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@user_id", owner);
        command.Parameters.AddWithValue("@source", deal.Source);
        command.Parameters.AddWithValue("@source_item_id", deal.SourceItemId);
        var found = command.ExecuteScalar();
        return found is null or DBNull ? null : Convert.ToInt64(found);
    }

    private static void Bind(SqliteCommand command, DealRecord deal)
    {
        command.Parameters.AddWithValue("@stage", deal.Stage);
        command.Parameters.AddWithValue("@title", deal.Title ?? "");
        command.Parameters.AddWithValue("@source", deal.Source ?? "manual");
        command.Parameters.AddWithValue("@source_label", deal.SourceLabel ?? "");
        command.Parameters.AddWithValue("@source_url", deal.SourceUrl ?? "");
        command.Parameters.AddWithValue("@source_item_id", deal.SourceItemId ?? "");
        command.Parameters.AddWithValue("@quantity", deal.Quantity);
        command.Parameters.AddWithValue("@ask_price", (object?)deal.AskPrice ?? DBNull.Value);
        command.Parameters.AddWithValue("@max_buy_price", (object?)deal.MaxBuyPrice ?? DBNull.Value);
        command.Parameters.AddWithValue("@projected_sale_price", (object?)deal.ProjectedSalePrice ?? DBNull.Value);
        command.Parameters.AddWithValue("@projected_net_profit", (object?)deal.ProjectedNetProfit ?? DBNull.Value);
        command.Parameters.AddWithValue("@projected_roi_percent", (object?)deal.ProjectedRoiPercent ?? DBNull.Value);
        command.Parameters.AddWithValue("@projected_days_to_cash", (object?)deal.ProjectedDaysToCash ?? DBNull.Value);
        command.Parameters.AddWithValue("@projected_basis", deal.ProjectedBasis ?? "");
        command.Parameters.AddWithValue("@projected_at", deal.ProjectedUtc?.ToString("O") ?? "");
        command.Parameters.AddWithValue("@purchase_price", (object?)deal.PurchasePrice ?? DBNull.Value);
        command.Parameters.AddWithValue("@purchase_extra_cost", deal.PurchaseExtraCost);
        command.Parameters.AddWithValue("@bought_at", deal.BoughtUtc?.ToString("O") ?? "");
        command.Parameters.AddWithValue("@listing_id", deal.ListingId ?? "");
        command.Parameters.AddWithValue("@sku", deal.Sku ?? "");
        command.Parameters.AddWithValue("@listed_price", (object?)deal.ListedPrice ?? DBNull.Value);
        command.Parameters.AddWithValue("@listed_at", deal.ListedUtc?.ToString("O") ?? "");
        command.Parameters.AddWithValue("@sold_at", deal.SoldUtc?.ToString("O") ?? "");
        command.Parameters.AddWithValue("@note", deal.Note ?? "");
        command.Parameters.AddWithValue("@created_at", deal.CreatedUtc.ToString("O"));
        command.Parameters.AddWithValue("@updated_at", deal.UpdatedUtc.ToString("O"));
        command.Parameters.AddWithValue("@stage_changed_at", deal.StageChangedUtc.ToString("O"));
    }

    private static DateTimeOffset? ReadDate(SqliteDataReader reader, int ordinal)
    {
        var raw = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(raw, out var value) ? value : null;
    }

    private static DealRecord Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Stage = DealStages.Normalize(reader.GetString(1)),
        Title = reader.GetString(2),
        Source = reader.GetString(3),
        SourceLabel = reader.GetString(4),
        SourceUrl = reader.GetString(5),
        SourceItemId = reader.GetString(6),
        Quantity = reader.GetInt32(7),
        AskPrice = reader.IsDBNull(8) ? null : reader.GetDecimal(8),
        MaxBuyPrice = reader.IsDBNull(9) ? null : reader.GetDecimal(9),
        ProjectedSalePrice = reader.IsDBNull(10) ? null : reader.GetDecimal(10),
        ProjectedNetProfit = reader.IsDBNull(11) ? null : reader.GetDecimal(11),
        ProjectedRoiPercent = reader.IsDBNull(12) ? null : reader.GetDecimal(12),
        ProjectedDaysToCash = reader.IsDBNull(13) ? null : reader.GetInt32(13),
        ProjectedBasis = reader.GetString(14),
        ProjectedUtc = ReadDate(reader, 15),
        PurchasePrice = reader.IsDBNull(16) ? null : reader.GetDecimal(16),
        PurchaseExtraCost = reader.GetDecimal(17),
        BoughtUtc = ReadDate(reader, 18),
        ListingId = reader.GetString(19),
        Sku = reader.GetString(20),
        ListedPrice = reader.IsDBNull(21) ? null : reader.GetDecimal(21),
        ListedUtc = ReadDate(reader, 22),
        SoldUtc = ReadDate(reader, 23),
        Note = reader.GetString(24),
        CreatedUtc = ReadDate(reader, 25) ?? DateTimeOffset.MinValue,
        UpdatedUtc = ReadDate(reader, 26) ?? DateTimeOffset.MinValue,
        StageChangedUtc = ReadDate(reader, 27) ?? DateTimeOffset.MinValue,
    };

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS deals (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                -- Whose board this card is on. 0 on a desktop database, where there is one seller.
                user_id INTEGER NOT NULL DEFAULT 0,
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

            CREATE INDEX IF NOT EXISTS ix_deals_stage ON deals(stage);
            CREATE INDEX IF NOT EXISTS ix_deals_listing ON deals(listing_id);
            """;
        command.ExecuteNonQuery();

        // The owner column, before the index that uses it — on an old database it does not exist yet.
        UserOwnedTable.Migrate(connection, "deals");

        using var indexes = connection.CreateCommand();
        indexes.CommandText = """
            -- What makes pressing "Track" twice on the same Craigslist post an update rather than
            -- a second copy of the same capital and the same projected profit — for the seller who
            -- pressed it. Another seller tracking that post gets their own card, not this one.
            DROP INDEX IF EXISTS ux_deals_source_item;

            CREATE UNIQUE INDEX IF NOT EXISTS ux_deals_user_source_item
                ON deals(user_id, source, source_item_id) WHERE source_item_id <> '';
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
