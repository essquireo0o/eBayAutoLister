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
    /// Whether buying from this source puts sales tax on the bill.
    ///
    /// A default rather than a required member, so no existing source had to be edited, and it
    /// defaults to <see cref="IsLocationBased"/>'s inverse because that is exactly what the UI was
    /// already inferring when the deal feeds were the only taxed source. The inference was only ever
    /// right by coincidence, and a liquidation auction breaks it: it is local AND it charges tax.
    /// </summary>
    bool ChargesSalesTax => !IsLocationBased;

    /// <summary>
    /// Sites this source deliberately does not read, offered to the seller as prefilled searches.
    ///
    /// Empty for every source that can read everything it claims to. It exists because the biggest
    /// names in liquidation — liquidation.com, B-Stock — answer an automated request with a block
    /// page rather than with stock, and the honest response to that is a link the seller can open,
    /// not a source chip that is red every time.
    /// </summary>
    IReadOnlyList<LocalSupplyManualSite> ManualSites => [];

    /// <summary>
    /// A radius this source will not search below, or 0 when it honours the form exactly.
    ///
    /// Exists so the UI can stop promising "within 40 miles" for a source that searched 250 —
    /// the same honesty <see cref="IsLocationBased"/> buys for the nationwide feeds. Auctions are
    /// the reason: a metro with four hundred classifieds for "dewalt" may have one liquidation
    /// sale running, and unlike a classified there is a whole pallet at the end of the drive.
    /// </summary>
    int MinRadiusMiles => 0;

    /// <summary>
    /// Whether searching this source with no keyword is meaningful.
    ///
    /// False for everything that existed before the freebie finder, and a default so none of them
    /// had to say so: a blank search of craigslist's for-sale board is the entire classifieds
    /// section, and a blank deal-feed scan is every discount in the country. On the free board it is
    /// the opposite — "show me everything being given away near me" is the single most useful search
    /// there is, because a seller shopping for free things does not care what they are.
    /// </summary>
    bool AllowsBlankQuery => false;

    /// <summary>
    /// Whether this source can be pointed at one category's own board rather than searched by
    /// keyword alone.
    /// </summary>
    /// <remarks>
    /// True for craigslist, which files cars, boats, RVs, trailers, appliances and furniture on
    /// separate boards — searching the right one is the difference between finding the local truck
    /// market and finding four listings that happened to contain the word "truck". False everywhere
    /// else, and a default so no existing source had to say so. The UI reads it to be honest about
    /// what a category actually did on each site, rather than implying every source narrowed.
    /// </remarks>
    bool SupportsCategoryBoards => false;

    /// <summary>
    /// One user-initiated search. Implementations must not retry, schedule, or authenticate as a
    /// side effect: an expired session is reported (status <c>session_expired</c>), never silently
    /// re-established.
    /// </summary>
    Task<LocalSupplySearchResult> SearchAsync(string query, string zip, int radiusMiles, CancellationToken ct = default);

    /// <summary>
    /// The same search, narrowed to a category. Defaults to ignoring the category entirely, which is
    /// the honest behaviour for a source that only has a search box — the scan still classifies what
    /// comes back per listing, so a keyword-only site contributes its cars to a cars board even
    /// though it couldn't be asked for them.
    /// </summary>
    Task<LocalSupplySearchResult> SearchAsync(
        string query, string zip, int radiusMiles, ResaleCategory category, CancellationToken ct = default) =>
        SearchAsync(query, zip, radiusMiles, ct);
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
        ChargesSalesTax = s.ChargesSalesTax, ManualSites = s.ManualSites.ToList(),
        MinRadiusMiles = s.MinRadiusMiles, AllowsBlankQuery = s.AllowsBlankQuery,
        SupportsCategoryBoards = s.SupportsCategoryBoards,
    }).ToList();
}
