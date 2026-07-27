namespace ING_eBay_AutoLister.Services;

/// <summary>
/// One slice of the liquidation-auction catalogue: the seller's own words, optionally narrowed by
/// a single extra term.
/// </summary>
/// <param name="Id">Stable id, used in the listing's ItemId prefix and in per-feed status rows.</param>
/// <param name="Label">How the feed is named in a status line.</param>
/// <param name="QueryTerm">
/// A term appended to the seller's query, or empty for "search exactly what they typed". One WORD,
/// never a phrase: the site ANDs every token, so <c>dyson v11 lot</c> matches and
/// <c>lot of dyson v11</c> matches nothing at all because of the "of" (verified live —
/// <c>lot of headphones</c> returned zero rows while <c>headphones lot</c> returned a full page).
/// </param>
/// <param name="Note">What this slice is for, in the status line when it finds nothing.</param>
public sealed record LiquidationFeed(string Id, string Label, string QueryTerm, string Note);

/// <summary>
/// A liquidation marketplace this app deliberately does NOT read, offered as a prefilled link.
/// </summary>
public sealed record LiquidationManualSite(string Id, string Label, string UrlTemplate, string Note);

/// <summary>
/// Every liquidation URL in one place, and the only file to edit when one of them moves.
///
/// <para><b>Why an auction aggregator and not liquidation.com.</b> The obvious names — liquidation.com,
/// B-Stock, Direct Liquidation — were all tried first and all of them answer an automated request
/// with a block page rather than with stock (liquidation.com returns a bare HTTP 403 for every
/// path; B-Stock answers with a Cloudflare interstitial). Building a source on top of that would
/// ship a permanently red chip. They are listed in <see cref="ManualSites"/> instead: a one-click
/// prefilled search the seller opens themselves, which is a smaller promise and one this app can
/// actually keep.</para>
///
/// <para><b>What is read instead.</b> HiBid is the aggregator the closing businesses themselves list
/// on: store-closing sales, business dispersals, overstock and customer-return auctions, municipal
/// and school surplus, all from thousands of independent auction houses. It publishes each search
/// as server-rendered state — a real, machine-readable document with the lot, the current bid, the
/// bid count, the closing time, the auction house, the pickup city AND the buyer's premium, which
/// is the one cost line that quietly ruins auction arithmetic. That is a far better answer than
/// scraped tiles, and it is why this source can price honestly at all.</para>
///
/// <para><b>Posture, identical to the other public sources</b> (CraigslistService, DealFeedService):
/// user-driven only, one request per slice per click, for the seller's own query, against the same
/// search page a person would open. Nothing scheduled, nothing crawled, nothing stored, no account,
/// no key, no retries past a refusal.</para>
/// </summary>
public static class LiquidationCatalog
{
    /// <summary>The id the source picker and the <c>sources=</c> parameter use.</summary>
    public const string SourceId = "liquidation";

    public const string SourceLabel = "Liquidation & closeouts";

    /// <summary>The aggregator behind the source, as the row badge names it.</summary>
    public const string Site = "HiBid";

    private const string SearchBase = "https://hibid.com/lots";

    /// <summary>
    /// Auctions are far sparser than classifieds — a metro with four hundred Craigslist posts for
    /// "dewalt" may have one liquidation sale running — and unlike a classified, an auction lot is
    /// worth a longer drive because there is a whole pallet at the end of it. So the seller's
    /// radius is a floor here, not a ceiling, and the scope label says exactly what was searched
    /// rather than quietly widening it behind their back.
    /// </summary>
    public const int MinRadiusMiles = 250;

    /// <summary>
    /// Ordered by how directly each answers the seller's question, because the scan budget is spent
    /// in this order: their own words first, then the two slices that surface multi-unit stock,
    /// then the closeout wording.
    /// </summary>
    public static readonly IReadOnlyList<LiquidationFeed> Feeds =
    [
        // Everything matching what they typed. Most of the money on this board is here and it is
        // single items: a working tool at a $5 opening bid in a business dispersal is the same
        // trade as a cheap Craigslist find, minus the stranger.
        new("hibid", "Auction lots", "",
            "Everything currently up for bid that matches your words."),

        // Multi-unit stock, which is what makes this board different from every other one in the
        // app: one bid buys eight of the thing instead of one.
        new("hibid-lot", "Multi-unit lots", "lot",
            "Lots of several units — priced per unit, not per lot."),

        new("hibid-pallet", "Pallets", "pallet",
            "Pallet-sized quantities. Sparse, and the biggest single tickets when it hits."),

        // The going-out-of-business half by name. Deliberately last: it is the narrowest slice and
        // the first to drop if these ever need trimming.
        new("hibid-closeout", "Closeouts", "closeout",
            "Closeout and end-of-line stock."),
    ];

    /// <summary>
    /// The liquidation marketplaces that block automated reads. Offered as prefilled searches so
    /// the seller who asked for liquidation.com gets liquidation.com — by hand, and said plainly —
    /// instead of a source chip that is red every time.
    /// </summary>
    public static readonly IReadOnlyList<LiquidationManualSite> ManualSites =
    [
        new("liquidation-com", "Liquidation.com",
            "https://www.liquidation.com/search?keywords={query}",
            "Pallets and truckloads direct from retailer returns. Blocks automated reads — open it yourself."),
        new("bstock", "B-Stock",
            "https://bstock.com/all-auctions/?search={query}",
            "The big-box liquidation marketplaces. Blocks automated reads — open it yourself."),
        new("direct-liquidation", "Direct Liquidation",
            "https://www.directliquidation.com/?s={query}",
            "Manifested pallets and truckloads. Blocks automated reads — open it yourself."),
    ];

    public static LiquidationFeed? ById(string? id) =>
        Feeds.FirstOrDefault(f => f.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The search this feed runs. <paramref name="zip"/> and <paramref name="radiusMiles"/> are
    /// sent only when there is a zip to send: without one the site searches nationwide, which is
    /// the right answer for a seller who left the field blank and a far better one than refusing
    /// to search at all.
    /// </summary>
    public static string BuildUrl(LiquidationFeed feed, string? query, string? zip, int radiusMiles)
    {
        var url = $"{SearchBase}?q={Uri.EscapeDataString(QueryFor(feed, query))}&status=OPEN";

        if (!string.IsNullOrWhiteSpace(zip))
            url += $"&zip={Uri.EscapeDataString(zip.Trim())}&miles={RadiusFor(radiusMiles)}";

        return url;
    }

    /// <summary>The words actually searched, which the status line quotes back.</summary>
    public static string QueryFor(LiquidationFeed feed, string? query)
    {
        var typed = (query ?? "").Trim();
        return feed.QueryTerm.Length == 0 ? typed : $"{typed} {feed.QueryTerm}";
    }

    public static int RadiusFor(int radiusMiles) => Math.Max(MinRadiusMiles, radiusMiles);

    /// <summary>The page to open when the seller wants to check the scan against the site itself.</summary>
    public static string SearchPageUrl(string? query, string? zip, int radiusMiles) =>
        BuildUrl(Feeds[0], query, zip, radiusMiles);

    /// <summary>
    /// What the results row says was actually searched. Names the widened radius explicitly: a scan
    /// that quietly searched 250 miles must never be reported as the 40 the form said.
    /// </summary>
    public static string ScopeLabel(string? zip, int radiusMiles)
    {
        if (string.IsNullOrWhiteSpace(zip))
            return $"{Site} — liquidation, closeout and surplus auctions, nationwide";

        var radius = RadiusFor(radiusMiles);
        var widened = radius > radiusMiles
            ? $" (widened from {radiusMiles} — auctions are far sparser than classifieds)"
            : "";

        return $"{Site} — auctions within {radius} mi of {zip.Trim()}{widened}";
    }
}
