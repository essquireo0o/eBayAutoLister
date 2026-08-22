using ING_eBay_AutoLister.Models;
using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Every AI resale estimate the app has ever made, kept in the app's own database.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The order a product gets priced in is settled — real sold comps
/// first, the AI only for what they could not answer (owner, 2026-08-21: "calculate products
/// using scraper first then AI … you save everything to the database"). The missing half was
/// the saving: every scan re-asked the model about the same forty listings, which is a
/// generation spent and fifteen seconds waited to be told again what a Razor moped sells for.
/// </para>
/// <para>
/// <b>What the key is.</b> The item's name, normalised — lowercased, punctuation folded to
/// spaces, whitespace collapsed. Two sellers listing "Razor Moped" and "razor  moped!!" are
/// asking the same question and deserve the same cached answer. The Marketplace item id is
/// deliberately NOT the key: the same product appears under a new id every time somebody
/// relists it, and a cache keyed that way would never hit.
/// </para>
/// <para>
/// <b>Why it expires.</b> Second-hand prices move — not by the hour, but a year-old estimate of
/// a phone is wrong. Thirty days is long enough that a board reloaded all week is free, and
/// short enough that nothing quietly rots. An expired row is left in place rather than deleted:
/// it costs nothing, and it is the record of what the app told the seller last month.
/// </para>
/// </remarks>
public sealed class AiEstimateStore
{
    /// <summary>How long an estimate is trusted before it is asked again.</summary>
    public static readonly TimeSpan GoodFor = TimeSpan.FromDays(30);

    private readonly string _databasePath;

    public AiEstimateStore(ListingDatabase database) : this(database.DatabasePath) { }

    public AiEstimateStore(string databasePath)
    {
        _databasePath = databasePath;
        Initialize();
    }

    /// <summary>The cache key for a name: what two people typing the same thing have in common.</summary>
    public static string KeyOf(string? title)
    {
        var text = (title ?? "").Trim().ToLowerInvariant();
        var kept = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
            kept.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        return string.Join(' ', kept.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// The estimates already known for these names, by the id the caller asked with. Names with
    /// no usable answer — never asked, or asked too long ago — are simply absent.
    /// </summary>
    public Dictionary<string, AiResaleEstimate> Known(IEnumerable<AiEstimateItem> items, DateTimeOffset now)
    {
        var found = new Dictionary<string, AiResaleEstimate>(StringComparer.Ordinal);
        var wanted = items
            .Where(i => !string.IsNullOrWhiteSpace(i.ItemId))
            .Select(i => (i.ItemId, Key: KeyOf(i.Title)))
            .Where(x => x.Key.Length > 0)
            .ToList();
        if (wanted.Count == 0) return found;

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT low, high, basis FROM ai_estimates WHERE key = @key AND at >= @since;";
        var keyParam = command.Parameters.Add("@key", SqliteType.Text);
        command.Parameters.AddWithValue("@since", (now - GoodFor).ToString("O"));

        foreach (var (itemId, key) in wanted)
        {
            keyParam.Value = key;
            using var reader = command.ExecuteReader();
            if (!reader.Read()) continue;
            found[itemId] = new AiResaleEstimate(
                itemId,
                Convert.ToDecimal(reader.GetDouble(0)),
                Convert.ToDecimal(reader.GetDouble(1)),
                reader.IsDBNull(2) ? "" : reader.GetString(2));
        }

        return found;
    }

    /// <summary>Remembers what the model said, so the next board does not have to ask again.</summary>
    public void Save(IEnumerable<AiEstimateItem> asked, IEnumerable<AiResaleEstimate> answers, DateTimeOffset now)
    {
        var titles = asked
            .Where(i => !string.IsNullOrWhiteSpace(i.ItemId))
            .GroupBy(i => i.ItemId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Title ?? "", StringComparer.Ordinal);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // One row per name: the newest answer replaces the older one rather than piling up.
        command.CommandText = """
            INSERT INTO ai_estimates (key, title, low, high, basis, at)
            VALUES (@key, @title, @low, @high, @basis, @at)
            ON CONFLICT(key) DO UPDATE SET
                low = excluded.low, high = excluded.high, basis = excluded.basis, at = excluded.at;
            """;
        var key = command.Parameters.Add("@key", SqliteType.Text);
        var title = command.Parameters.Add("@title", SqliteType.Text);
        var low = command.Parameters.Add("@low", SqliteType.Real);
        var high = command.Parameters.Add("@high", SqliteType.Real);
        var basis = command.Parameters.Add("@basis", SqliteType.Text);
        command.Parameters.AddWithValue("@at", now.ToString("O"));

        foreach (var answer in answers)
        {
            if (!titles.TryGetValue(answer.ItemId, out var name)) continue;
            var k = KeyOf(name);
            if (k.Length == 0 || answer.Low <= 0 || answer.High <= 0) continue;
            key.Value = k;
            title.Value = name;
            low.Value = (double)answer.Low;
            high.Value = (double)answer.High;
            basis.Value = answer.Basis ?? "";
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>How many estimates are on file, and how many are still trusted.</summary>
    public (int Total, int Fresh) Counts(DateTimeOffset now)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), SUM(CASE WHEN at >= @since THEN 1 ELSE 0 END) FROM ai_estimates;";
        command.Parameters.AddWithValue("@since", (now - GoodFor).ToString("O"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return (0, 0);
        return (reader.GetInt32(0), reader.IsDBNull(1) ? 0 : reader.GetInt32(1));
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ai_estimates (
                -- The normalised name. One row per product, not per listing.
                key TEXT PRIMARY KEY,
                -- What was actually typed, kept for reading the table back as a human.
                title TEXT NOT NULL DEFAULT '',
                low REAL NOT NULL,
                high REAL NOT NULL,
                basis TEXT NOT NULL DEFAULT '',
                at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_ai_estimates_at ON ai_estimates(at);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        connection.Open();
        return connection;
    }
}
