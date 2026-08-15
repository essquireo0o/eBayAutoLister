using System.Text.Json;

namespace ING_eBay_AutoLister.Services;

// ── Keeping Amazon's schemas on disk ──────────────────────────────────────────────────────────
//
// eBay's aspect cache (EbayService._aspectCache) is a dictionary in memory with a 12-hour life,
// and that is right for what it holds: a few dozen aspect names, re-fetchable in one small call.
//
// An Amazon product type schema is neither of those things. It is a JSON Schema document — tens to
// hundreds of kilobytes, a couple of hundred attributes, every enum Amazon accepts for each — and
// it is fetched in TWO hops: an SP-API call for the definition, then a second request to a
// pre-signed URL for the document itself. It also barely ever changes; Amazon versions it, and the
// version only moves when the requirements actually move.
//
// So this one goes to disk, and it is keyed by VERSION rather than aged by a clock. The definition
// call is small and is made every time, which is what makes that possible: it returns the current
// version and checksum for a few hundred bytes, so the app can learn that the large document it
// already has is still the current one without downloading it again.
//
// The time limit that remains is a backstop, not the mechanism. See Ttl.

/// <summary>One cached schema and what it was current for.</summary>
/// <param name="Version">Amazon's opaque product type version. The identity that matters.</param>
/// <param name="Checksum">Amazon's checksum of the document, carried so a mismatch can be noticed.</param>
public sealed record AmazonCachedSchema(
    string ProductType,
    string MarketplaceId,
    string Requirements,
    string Locale,
    string Version,
    string Checksum,
    DateTimeOffset FetchedAt,
    string Schema);

/// <summary>
/// The on-disk store of Amazon product type schemas.
/// </summary>
/// <remarks>
/// <para>
/// Written through <see cref="AtomicFile"/>, so a crash mid-write cannot leave a half-written
/// schema that parses into a product type with three required attributes instead of nine. A file
/// that is unreadable for any reason is simply a miss — a corrupt cache must never be able to fail
/// a lookup, only to make it slower.
/// </para>
/// <para>
/// Nothing here is a secret and nothing here is per-seller: a product type schema is Amazon's
/// public description of a category, identical for every seller in a marketplace. That is why it
/// can sit in the app's data folder at all, and it is worth stating because the folder beside it
/// holds credentials.json.
/// </para>
/// </remarks>
public sealed class AmazonSchemaCache
{
    /// <summary>Folder under the data home. Sits beside App_Data rather than in it — this is a cache.</summary>
    public const string FolderName = "amazon-schemas";

    /// <summary>
    /// How long a cached schema is served when Amazon's current version is UNKNOWN.
    /// </summary>
    /// <remarks>
    /// Not the expiry of a good entry. A hit is decided by version, so a schema Amazon has not
    /// changed stays valid indefinitely and is never re-downloaded. This applies only where the
    /// definition call failed and the choice is between a schema from disk and no answer at all:
    /// thirty days is long enough to ride out an outage and short enough that a listing is never
    /// built against requirements from a different season.
    /// </remarks>
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(30);

    private readonly string _root;
    private readonly object _gate = new();

    public AmazonSchemaCache() : this(Path.Combine(AppPaths.DataHome, FolderName)) { }

    /// <summary>Root-injected overload, so the tests never write into the seller's data folder.</summary>
    public AmazonSchemaCache(string root) => _root = root;

    public string Root => _root;

    /// <summary>
    /// Where one schema lives.
    /// </summary>
    /// <remarks>
    /// Every part of the key is in the file name because every one of them changes the answer: the
    /// same product type has different attributes in different marketplaces, different labels in
    /// different locales, and a different attribute set for LISTING than for LISTING_OFFER_ONLY.
    /// Sanitised because a product type name arrives from a response — nothing from Amazon gets to
    /// name a path segment on this machine.
    /// </remarks>
    public string PathFor(string productType, string marketplaceId, string requirements, string locale) =>
        Path.Combine(_root,
            $"{Safe(productType)}.{Safe(marketplaceId)}.{Safe(requirements)}.{Safe(locale)}.json");

    /// <summary>
    /// The cached schema for this key, or null when there is none this app can trust.
    /// </summary>
    /// <param name="version">
    /// Amazon's current version, when it is known. Given one, an entry recording a different
    /// version is a miss no matter how recent it is — Amazon changed the requirements, and a
    /// listing built from the old ones is a listing built to be rejected. Pass null when the
    /// definition call failed, and <see cref="Ttl"/> decides instead.
    /// </param>
    public AmazonCachedSchema? Read(
        string productType, string marketplaceId, string requirements, string locale,
        string? version = null, DateTimeOffset? now = null)
    {
        var path = PathFor(productType, marketplaceId, requirements, locale);

        string? json;
        lock (_gate)
        {
            if (!File.Exists(path)) return null;
            json = AtomicFile.ReadWithRecovery(path);
        }
        if (string.IsNullOrWhiteSpace(json)) return null;

        AmazonCachedSchema? entry;
        try
        {
            entry = JsonSerializer.Deserialize<AmazonCachedSchema>(json);
        }
        catch (JsonException)
        {
            // Unreadable. A miss, not a failure — the schema is re-fetchable and this file is not
            // anything's only copy.
            return null;
        }

        if (entry is null || string.IsNullOrWhiteSpace(entry.Schema)) return null;

        if (!string.IsNullOrWhiteSpace(version))
            return string.Equals(entry.Version, version, StringComparison.Ordinal) ? entry : null;

        return (now ?? DateTimeOffset.UtcNow) - entry.FetchedAt <= Ttl ? entry : null;
    }

    /// <summary>
    /// Stores one schema. Never throws: a cache that cannot be written is a slower app, and a
    /// lookup that already succeeded must not be turned into a failure by the saving of it.
    /// </summary>
    public bool Write(AmazonCachedSchema entry)
    {
        if (string.IsNullOrWhiteSpace(entry.ProductType) || string.IsNullOrWhiteSpace(entry.Schema))
            return false;

        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_root);
                AtomicFile.WriteAllText(
                    PathFor(entry.ProductType, entry.MarketplaceId, entry.Requirements, entry.Locale),
                    JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = false }));
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>How many schemas are held, for the diagnostic to report.</summary>
    public int Count()
    {
        try { return Directory.Exists(_root) ? Directory.GetFiles(_root, "*.json").Length : 0; }
        catch (IOException) { return 0; }
    }

    /// <summary>Anything outside this is not going in a file name, whatever Amazon called it.</summary>
    private static string Safe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unset";
        var safe = new string(value.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        return safe.Length <= 80 ? safe : safe[..80];
    }
}
