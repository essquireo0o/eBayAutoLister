using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// One place local inventory can be read from: Craigslist, Facebook Marketplace, and whatever
/// comes next (OfferUp is the obvious third).
///
/// The point of the interface is that the expensive half of the feature — grouping listings into
/// products, one sold-comp lookup per product, the ProfitCalculator pass and the ranking — is
/// written once against <see cref="LocalSupplyListing"/> and never learns which site a row came
/// from. Adding a source is implementing this and registering it; nothing downstream changes.
///
/// Sources differ in exactly one way that callers care about, and it's modelled here rather than
/// special-cased: some need a saved login (Facebook) and some are public (Craigslist).
/// </summary>
public interface ILocalSupplySource
{
    /// <summary>Stable id used in API parameters and stored UI preferences: craigslist | facebook.</summary>
    string Id { get; }

    string Label { get; }

    /// <summary>True when the source can only be searched through a saved browser session.</summary>
    bool RequiresConnection { get; }

    /// <summary>
    /// Whether a search would actually reach the site right now. Public sources are always
    /// available; session-based ones are available only while a session is saved.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Why it isn't available, or what it costs — shown next to the source's checkbox.</summary>
    string AvailabilityNote { get; }

    /// <summary>
    /// Whether the zip and radius on the search form mean anything to this source.
    ///
    /// False for the online ones (retail deal feeds ship nationwide), and a default rather than a
    /// required member because every source that existed before one of those did is location-based
    /// and shouldn't have to say so. What it buys is honesty in the UI: a scan that searched the
    /// whole country must not be reported as "within 40 miles of 89101", and a seller with no local
    /// supply at all must not be told to enter a zip code before they can look.
    /// </summary>
    bool IsLocationBased => true;

    /// <summary>
    /// One user-initiated search. Implementations must not retry, schedule, or authenticate as a
    /// side effect: an expired session is reported (status <c>session_expired</c>), never silently
    /// re-established.
    /// </summary>
    Task<LocalSupplySearchResult> SearchAsync(string query, string zip, int radiusMiles, CancellationToken ct = default);
}

/// <summary>
/// The registry behind the source picker. Resolves the <c>sources=</c> parameter, and decides
/// what "no preference" means: everything that can answer right now.
/// </summary>
public sealed class LocalSupplySources(IEnumerable<ILocalSupplySource> sources)
{
    // Registration order is deliberate (see Program.cs): public, no-login sources first, so a
    // default search returns something for a seller who has connected nothing.
    public IReadOnlyList<ILocalSupplySource> All { get; } = sources.ToList();

    public ILocalSupplySource? ById(string id) =>
        All.FirstOrDefault(s => s.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves a comma-separated source list. An explicitly named source is returned even when
    /// it isn't available — its <c>not_connected</c> status is what makes the UI show a connect
    /// prompt, which is more useful than silently dropping the site the seller asked for.
    /// Unknown names are ignored rather than failing the whole search.
    /// </summary>
    public IReadOnlyList<ILocalSupplySource> Resolve(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return All.Where(s => s.IsAvailable).ToList();

        var wanted = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var picked = All.Where(s => wanted.Contains(s.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        return picked.Count > 0 ? picked : All.Where(s => s.IsAvailable).ToList();
    }

    public List<LocalSupplySourceInfo> Describe() => All.Select(s => new LocalSupplySourceInfo
    {
        Id = s.Id, Label = s.Label, RequiresConnection = s.RequiresConnection,
        Available = s.IsAvailable, Note = s.AvailabilityNote, LocationBased = s.IsLocationBased,
    }).ToList();
}
