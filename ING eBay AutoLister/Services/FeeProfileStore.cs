using System.Globalization;
using ING_eBay_AutoLister.Models;
using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Persists the seller's real fee and cost assumptions, and keeps the live
/// <see cref="FeeProfile"/> singleton in step with them.
/// </summary>
/// <remarks>
/// <para>
/// Until this existed, <see cref="FeeProfile"/> was a set of hardcoded defaults with shipping,
/// packaging, handling, promoted-ad rate and return reserve all sitting at zero, and no way to
/// change them. Every "net profit" the app printed was therefore net of eBay's cut and nothing
/// else — which is precisely the gap sellers lose money in. A $9 label and a 2% ad rate on a $120
/// sale is $11.40 that the old numbers said the seller kept.
/// </para>
/// <para>
/// One row in the app's own SQLite database, alongside <see cref="CostBasisStore"/>, rather than a
/// settings file: this belongs with the cost basis it is combined with on every screen, and it has
/// to survive a reinstall the same way. Values are stored one per row so a profile written by an
/// older build simply lacks the newer keys and falls back to their defaults, instead of failing to
/// deserialise.
/// </para>
/// </remarks>
public sealed class FeeProfileStore
{
    private readonly string _databasePath;
    private readonly object _gate = new();

    public FeeProfileStore(ListingDatabase database) : this(database.DatabasePath) { }

    public FeeProfileStore(string databasePath)
    {
        _databasePath = databasePath;
        Initialize();
    }

    /// <summary>The stored profile, or the built-in defaults where nothing has been saved.</summary>
    public FeeProfile Load()
    {
        var values = ReadAll();
        var profile = new FeeProfile();

        decimal Get(string key, decimal fallback) =>
            values.TryGetValue(key, out var raw) && decimal.TryParse(raw,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value : fallback;

        profile.EbayFinalValueFeePercent = Get("ebay_fvf_percent", profile.EbayFinalValueFeePercent);
        profile.EbayFinalValueFeeFixed = Get("ebay_fvf_fixed", profile.EbayFinalValueFeeFixed);
        profile.PromotedListingRatePercent = Get("promoted_percent", profile.PromotedListingRatePercent);
        profile.PaymentProcessingPercent = Get("payment_percent", profile.PaymentProcessingPercent);
        profile.DefaultShippingCost = Get("shipping_cost", profile.DefaultShippingCost);
        profile.DefaultPackagingCost = Get("packaging_cost", profile.DefaultPackagingCost);
        profile.DefaultLaborCost = Get("labor_cost", profile.DefaultLaborCost);
        profile.ReturnReservePercent = Get("return_reserve_percent", profile.ReturnReservePercent);
        profile.TestingReservePercent = Get("testing_reserve_percent", profile.TestingReservePercent);
        profile.MinimumNetProfit = Get("minimum_net_profit", profile.MinimumNetProfit);
        profile.MinimumMarginPercent = Get("minimum_margin_percent", profile.MinimumMarginPercent);

        profile.Sanitize();
        return profile;
    }

    /// <summary>
    /// Writes a profile and returns the sanitized version that was actually stored.
    /// </summary>
    public FeeProfile Save(FeeProfile profile)
    {
        var clean = profile.Clone();
        clean.Sanitize();

        var values = new Dictionary<string, decimal>
        {
            ["ebay_fvf_percent"] = clean.EbayFinalValueFeePercent,
            ["ebay_fvf_fixed"] = clean.EbayFinalValueFeeFixed,
            ["promoted_percent"] = clean.PromotedListingRatePercent,
            ["payment_percent"] = clean.PaymentProcessingPercent,
            ["shipping_cost"] = clean.DefaultShippingCost,
            ["packaging_cost"] = clean.DefaultPackagingCost,
            ["labor_cost"] = clean.DefaultLaborCost,
            ["return_reserve_percent"] = clean.ReturnReservePercent,
            ["testing_reserve_percent"] = clean.TestingReservePercent,
            ["minimum_net_profit"] = clean.MinimumNetProfit,
            ["minimum_margin_percent"] = clean.MinimumMarginPercent,
        };

        lock (_gate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var stamp = DateTimeOffset.UtcNow.ToString("O");

            foreach (var (key, value) in values)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO fee_profile (key, value, updated_at) VALUES (@key, @value, @updated_at)
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("@key", key);
                command.Parameters.AddWithValue("@value", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("@updated_at", stamp);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        return clean;
    }

    /// <summary>
    /// Saves, then overwrites the DI singleton in place so every screen re-costs against the new
    /// numbers without a restart. The mutation is the point: LocalArbitrageAnalyzer, LotAnalyzer,
    /// InventoryHealthAnalyzer and JackpotHunter all receive the same instance.
    /// </summary>
    public FeeProfile SaveAndApply(FeeProfile incoming, FeeProfile live)
    {
        var stored = Save(incoming);
        live.CopyFrom(stored);
        return stored;
    }

    /// <summary>Reads the stored profile into the live singleton. Called once at startup.</summary>
    public FeeProfile Apply(FeeProfile live)
    {
        var stored = Load();
        live.CopyFrom(stored);
        return stored;
    }

    // ── The Tax Pack's one seller-supplied number ────────────────────────────────────────────
    // The marginal income-tax bracket is the only figure the Tax Pack cannot derive, and asking for
    // it on every visit would make the screen feel like a form. It lives in this table rather than
    // on FeeProfile because FeeProfile round-trips through the Fees & Costs screen, which knows
    // nothing about tax — saving fees there would quietly reset the bracket to the default.
    private const string TaxRateKey = "tax_income_rate_percent";

    /// <summary>The seller's stored bracket, or 12% — the band most side-income resellers sit in.</summary>
    public decimal LoadTaxRate()
    {
        var values = ReadAll();
        if (values.TryGetValue(TaxRateKey, out var raw)
            && decimal.TryParse(raw, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            return Math.Clamp(value, 0m, 50m);

        return new TaxAssumptions().IncomeTaxRatePercent;
    }

    /// <summary>Stores the bracket and returns what was actually kept after clamping.</summary>
    public decimal SaveTaxRate(decimal ratePercent)
    {
        var clean = Math.Clamp(Math.Round(ratePercent, 2), 0m, 50m);

        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO fee_profile (key, value, updated_at) VALUES (@key, @value, @updated_at)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("@key", TaxRateKey);
            command.Parameters.AddWithValue("@value", clean.ToString(System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@updated_at", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        return clean;
    }


    // ── Mapping to and from the wire shape ───────────────────────────────────────────────────

    public static FeeProfileView ToView(FeeProfile profile) => new()
    {
        EbayFinalValueFeePercent = profile.EbayFinalValueFeePercent,
        EbayFinalValueFeeFixed = profile.EbayFinalValueFeeFixed,
        PromotedListingRatePercent = profile.PromotedListingRatePercent,
        PaymentProcessingPercent = profile.PaymentProcessingPercent,
        DefaultShippingCost = profile.DefaultShippingCost,
        DefaultPackagingCost = profile.DefaultPackagingCost,
        DefaultLaborCost = profile.DefaultLaborCost,
        ReturnReservePercent = profile.ReturnReservePercent,
        TestingReservePercent = profile.TestingReservePercent,
        MinimumNetProfit = profile.MinimumNetProfit,
        MinimumMarginPercent = profile.MinimumMarginPercent,
        RevenueFeePercent = Math.Round(profile.RevenueFeeFraction * 100m, 2),
    };

    public static FeeProfile FromView(FeeProfileView view)
    {
        var profile = new FeeProfile
        {
            EbayFinalValueFeePercent = view.EbayFinalValueFeePercent,
            EbayFinalValueFeeFixed = view.EbayFinalValueFeeFixed,
            PromotedListingRatePercent = view.PromotedListingRatePercent,
            PaymentProcessingPercent = view.PaymentProcessingPercent,
            DefaultShippingCost = view.DefaultShippingCost,
            DefaultPackagingCost = view.DefaultPackagingCost,
            DefaultLaborCost = view.DefaultLaborCost,
            ReturnReservePercent = view.ReturnReservePercent,
            TestingReservePercent = view.TestingReservePercent,
            MinimumNetProfit = view.MinimumNetProfit,
            MinimumMarginPercent = view.MinimumMarginPercent,
        };
        profile.Sanitize();
        return profile;
    }

    /// <summary>One upsert per key inside one transaction, so a half-written setting is not a state.</summary>
    private void WriteValues(IReadOnlyDictionary<string, string> values)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var stamp = DateTimeOffset.UtcNow.ToString("O");

            foreach (var (key, value) in values)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO fee_profile (key, value, updated_at) VALUES (@key, @value, @updated_at)
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("@key", key);
                command.Parameters.AddWithValue("@value", value);
                command.Parameters.AddWithValue("@updated_at", stamp);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    private Dictionary<string, string> ReadAll()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM fee_profile;";

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read()) values[reader.GetString(0)] = reader.GetString(1);
        return values;
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS fee_profile (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL DEFAULT '0',
                updated_at TEXT NOT NULL DEFAULT ''
            );
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
