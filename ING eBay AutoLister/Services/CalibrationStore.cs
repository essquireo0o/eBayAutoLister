using System.Text.Json;
using System.Text.Json.Serialization;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// One comp-depth bucket's measured forecasting bias, and how many holdouts it rests on.
/// </summary>
/// <param name="BiasPct">
/// The median of <c>(predicted - actual) / actual * 100</c> across the arb-bot's backtests for this
/// bucket. Positive means forecasts ran high (the app over-values), negative means they ran low.
/// </param>
/// <param name="N">How many holdout backtests produced <see cref="BiasPct"/>. The sample gate reads this.</param>
public sealed class CalibrationBucket
{
    public double BiasPct { get; set; }
    public int N { get; set; }
}

/// <summary>
/// The latest self-calibration the arb-bot produced: the systematic bias it measured by backtesting
/// the median-of-comps predictor against held-out sales, bucketed by how many comps were behind each
/// forecast. Persisted per deployment and read by <see cref="MarketPriceEstimator"/> to correct a
/// measured over- or under-estimate.
/// </summary>
/// <remarks>
/// This is a plain data record, filled from JSON the bot POSTs. The bot makes ZERO AI calls — this
/// is pure statistics — and the app only ever multiplies a price by a bounded, sample-gated factor
/// derived from it. An empty/absent calibration leaves pricing exactly as it was.
/// </remarks>
public sealed class CalibrationData
{
    public DateTimeOffset? GeneratedUtc { get; set; }
    public int SampleSize { get; set; }
    public double OverallBiasPct { get; set; }

    /// <summary>
    /// Bias by comp-depth bucket, keyed by the canonical names <c>thin</c> / <c>mid</c> / <c>deep</c>.
    /// The bot writes friendlier labels (<c>"thin(&lt;8 comps)"</c>); <see cref="Parse"/> normalizes them.
    /// </summary>
    public Dictionary<string, CalibrationBucket> Buckets { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public string Predictor { get; set; } = "";
}

/// <summary>The correction <see cref="MarketPriceEstimator"/> should apply to one price.</summary>
/// <param name="Applied">False means "leave the price alone" — no data, sample too small, or gated off.</param>
/// <param name="Factor">Multiplier for ExpectedSalePrice. 1.0 when not applied. Always in [0.80, 1.20].</param>
/// <param name="Bucket">Which comp-depth bucket was used.</param>
/// <param name="BiasPct">The bias the factor corrects for.</param>
/// <param name="N">The bucket's sample size.</param>
public readonly record struct CorrectionResult(
    bool Applied, decimal Factor, string Bucket, double BiasPct, int N)
{
    public static readonly CorrectionResult None = new(false, 1.0m, "", 0, 0);
}

/// <summary>
/// A small per-deployment JSON store for the latest arb-bot calibration, kept beside the app's other
/// App_Data state. Load/save the whole record, and resolve the bounded correction factor for a price.
/// </summary>
/// <remarks>
/// <para>
/// Follows the house pattern of the other small stores: a JSON file under <c>App_Data</c>, written
/// atomically (see <see cref="AtomicFile"/>) so a crash mid-write never leaves the app reading a
/// half-file, and every read/parse is defensive — bad input is refused, never thrown at the caller,
/// because this feeds live pricing and the safe failure is "change nothing".
/// </para>
/// <para>
/// The correction it hands out is deliberately conservative: it only acts on a bucket backed by at
/// least <see cref="MinBucketSample"/> holdouts, and the multiplier is clamped to
/// [<see cref="MinFactor"/>, <see cref="MaxFactor"/>] so no calibration can ever move a price by more
/// than 20%. An empty store yields <see cref="CorrectionResult.None"/> — pricing is untouched.
/// </para>
/// </remarks>
public sealed class CalibrationStore
{
    /// <summary>Below this many holdouts in a bucket, its bias is not trusted enough to act on.</summary>
    public const int MinBucketSample = 30;

    /// <summary>The correction can never cut a price by more than 20%.</summary>
    public const decimal MinFactor = 0.80m;

    /// <summary>The correction can never raise a price by more than 20%.</summary>
    public const decimal MaxFactor = 1.20m;

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly object _gate = new();
    private CalibrationData? _current;

    public CalibrationStore(IWebHostEnvironment env) : this(PathUnder(env)) { }

    public CalibrationStore(string filePath)
    {
        _filePath = filePath;
        _current = Load();
    }

    private static string PathUnder(IWebHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "calibration.json");
    }

    /// <summary>The latest stored calibration, or null when none has ever been saved.</summary>
    public CalibrationData? Current => _current;

    /// <summary>Reads the calibration from disk, or null when there is none / it is unreadable.</summary>
    public CalibrationData? Load()
    {
        var text = AtomicFile.ReadWithRecovery(_filePath, IsParseable);
        return text is null ? null : Parse(text);
    }

    /// <summary>
    /// Sanitizes and persists a calibration, updates the in-memory copy, and returns what was stored.
    /// </summary>
    public CalibrationData Save(CalibrationData data)
    {
        var clean = Sanitize(data);
        lock (_gate)
        {
            AtomicFile.WriteAllText(_filePath, JsonSerializer.Serialize(clean, WriteOpts));
            _current = clean;
        }
        return clean;
    }

    /// <summary>
    /// The bounded, sample-gated correction for a price backed by <paramref name="compCount"/> comps.
    /// Returns <see cref="CorrectionResult.None"/> (factor 1.0) whenever there is nothing trustworthy
    /// to act on — no calibration, no matching bucket, or a bucket below the sample gate.
    /// </summary>
    public CorrectionResult ResolveCorrection(int compCount)
    {
        var data = _current;
        if (data is null) return CorrectionResult.None;

        var key = BucketKeyFor(compCount);
        if (!data.Buckets.TryGetValue(key, out var bucket) || bucket is null)
            return CorrectionResult.None;
        if (bucket.N < MinBucketSample) return CorrectionResult.None;
        if (double.IsNaN(bucket.BiasPct) || double.IsInfinity(bucket.BiasPct)) return CorrectionResult.None;

        // Correct a systematic error: if forecasts ran +biasPct high, divide it back out. A bias at
        // or below -100% would make the denominator non-positive; clamp handles the rest, but the
        // division itself must be guarded, so that pathological case is pinned to the ceiling.
        var denom = 1.0m + (decimal)bucket.BiasPct / 100m;
        var raw = denom <= 0m ? MaxFactor : 1.0m / denom;
        var factor = Math.Clamp(raw, MinFactor, MaxFactor);

        return new CorrectionResult(true, factor, key, bucket.BiasPct, bucket.N);
    }

    /// <summary>Which comp-depth bucket a forecast off <paramref name="compCount"/> comps falls in.</summary>
    public static string BucketKeyFor(int compCount) =>
        compCount < 8 ? "thin" : compCount <= 20 ? "mid" : "deep";

    /// <summary>
    /// Parses the calibration JSON the bot POSTs, normalizing bucket labels to <c>thin/mid/deep</c>.
    /// Returns null for anything that will not deserialize — a bad body must never throw here.
    /// </summary>
    public static CalibrationData? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var raw = JsonSerializer.Deserialize<CalibrationData>(json, ReadOpts);
            return raw is null ? null : Sanitize(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsParseable(string text) => Parse(text) is not null;

    /// <summary>
    /// Returns a clean copy: bucket labels normalized to canonical keys, non-finite numbers dropped,
    /// counts floored at zero. Never throws.
    /// </summary>
    private static CalibrationData Sanitize(CalibrationData data)
    {
        var clean = new CalibrationData
        {
            GeneratedUtc = data.GeneratedUtc,
            SampleSize = Math.Max(0, data.SampleSize),
            OverallBiasPct = Finite(data.OverallBiasPct),
            Predictor = (data.Predictor ?? "").Trim(),
            Buckets = new Dictionary<string, CalibrationBucket>(StringComparer.OrdinalIgnoreCase),
        };

        foreach (var (label, bucket) in data.Buckets)
        {
            if (bucket is null) continue;
            var key = NormalizeBucketKey(label);
            if (key is null) continue;
            clean.Buckets[key] = new CalibrationBucket
            {
                BiasPct = Finite(bucket.BiasPct),
                N = Math.Max(0, bucket.N),
            };
        }

        return clean;
    }

    private static double Finite(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? 0.0 : value;

    /// <summary>
    /// Maps a bucket label — the bot's <c>"thin(&lt;8 comps)"</c>, or an already-canonical
    /// <c>"thin"</c> — onto one of the three canonical keys, or null if it names none of them.
    /// </summary>
    private static string? NormalizeBucketKey(string? label)
    {
        var l = (label ?? "").ToLowerInvariant();
        if (l.Contains("thin")) return "thin";
        if (l.Contains("mid")) return "mid";
        if (l.Contains("deep")) return "deep";
        return null;
    }
}
