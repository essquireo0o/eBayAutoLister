namespace ING_eBay_AutoLister.Services;

/// <summary>
/// One public deal feed: where to fetch it, what to call the site it came from, and whether the
/// site does the searching or we do.
/// </summary>
/// <param name="Id">Stable id, used in the listing's ItemId prefix and in per-feed status rows.</param>
/// <param name="Site">What the row badge says — the aggregator the deal was found on.</param>
/// <param name="Label">How the feed is named in a status line, when several feeds share a site.</param>
/// <param name="UrlTemplate">
/// The feed URL. A template containing <c>{query}</c> is a KEYWORD SEARCH the site runs itself;
/// anything else is a browse feed of whatever is hot right now, which this app filters locally.
/// </param>
/// <param name="HomeUrl">The human page to open when the seller wants to see the feed themselves.</param>
public sealed record DealFeed(string Id, string Site, string Label, string UrlTemplate, string HomeUrl)
{
    /// <summary>
    /// True when the aggregator searched for the seller's words itself.
    ///
    /// This is the single most important property of a feed, because it decides whether the
    /// keyword filter runs: a site that already searched has earned the same trust Craigslist gets
    /// (its idea of a match is better than a substring test), while a firehose of 600 unrelated
    /// deals has to be filtered hard or the ranking fills with cereal.
    /// </summary>
    public bool IsKeywordSearch => UrlTemplate.Contains("{query}", StringComparison.Ordinal);
}

/// <summary>
/// Every deal-aggregator URL in one place, and the only file to edit when one of them moves.
///
/// These are the retail half of arbitrage: Slickdeals, DealNews and TechBargains publish what is
/// discounted at Amazon, Walmart, Best Buy and the rest right now, as plain public RSS — no
/// account, no key, no browser. That makes a "buy it new on clearance, sell it used-market on
/// eBay" scan exactly as cheap as the Craigslist one, and it is the only sourcing screen in this
/// app that works for a seller with no local supply at all.
///
/// Two kinds of feed are listed here on purpose, because they fail in opposite directions:
///   • <b>Keyword search</b> (Slickdeals' <c>newsearch.php</c>) answers the seller's actual words,
///     but only covers one site.
///   • <b>Browse feeds</b> (frontpage, clearance, DealNews, TechBargains) cover far more ground but
///     answer nobody's question, so <see cref="DealFeedParser"/> filters them against the query.
///
/// Adding a source is one entry here. Nothing else in the pipeline learns that it exists — the
/// parser reads by element name across RSS dialects, and everything downstream of
/// <see cref="DealFeedService"/> is written against <c>LocalSupplyListing</c>.
///
/// Posture, identical to the other public source (CraigslistService): user-driven only, one
/// request per feed per click, nothing scheduled, nothing crawled, nothing stored.
/// </summary>
public static class DealFeedCatalog
{
    /// <summary>The id the source picker and the <c>sources=</c> parameter use.</summary>
    public const string SourceId = "dealfeeds";

    public const string SourceLabel = "Retail deal feeds";

    /// <summary>
    /// Ordered by how well each answers the seller's actual question, because the analysis cap is
    /// spent in this order when a search returns more than it can price: the site that searched for
    /// the seller's words first, then the hand-vetted front page, then clearance, then the
    /// firehoses.
    /// </summary>
    public static readonly IReadOnlyList<DealFeed> Feeds =
    [
        // The only feed that searches. Slickdeals' RSS honours ?q= server-side, which is why it
        // leads: everything below is a fixed list this app has to filter down to the query.
        new("slickdeals", "Slickdeals", "Slickdeals search",
            "https://slickdeals.net/newsearch.php?q={query}&searcharea=deals&searchin=first&rss=1",
            "https://slickdeals.net/"),

        // The front page is the community's own filter — deals that got enough votes to be
        // promoted. Small, high signal, and where the genuinely underpriced items surface.
        new("slickdeals-frontpage", "Slickdeals", "Slickdeals front page",
            "https://slickdeals.net/newsearch.php?mode=frontpage&searcharea=deals&searchin=first&rss=1",
            "https://slickdeals.net/deals/"),

        // Clearance and closeout specifically: end-of-line stock at a price that no longer reflects
        // what the thing still sells for used. That gap is the whole trade this screen exists for.
        new("slickdeals-clearance", "Slickdeals", "Slickdeals clearance & closeout",
            "https://slickdeals.net/newsearch.php?q=clearance+closeout&searcharea=deals&searchin=first&rss=1",
            "https://slickdeals.net/"),

        // DealNews is editorially curated and — uniquely among these — publishes structured fields:
        // a real price, the retailer, and a deal type that says whether an entry is one buyable item
        // or a whole category sale. See DealFeedParser, which trusts those over any title parsing.
        new("dealnews", "DealNews", "DealNews today's deals",
            "https://www.dealnews.com/?rss=1&sort=time",
            "https://www.dealnews.com/"),

        // Electronics specifically, because that is where resale value concentrates and the
        // all-categories feed above is shallow (roughly 45 entries covering everything).
        new("dealnews-electronics", "DealNews", "DealNews electronics",
            "https://www.dealnews.com/c142/Electronics/?rss=1",
            "https://www.dealnews.com/c142/Electronics/"),

        // The deepest feed of the set (hundreds of live deals) and the most clearance-heavy, which
        // is also why it is last: it is filtered hardest and it is the one to drop first if these
        // ever need trimming.
        new("techbargains", "TechBargains", "TechBargains",
            "https://www.techbargains.com/rss.xml",
            "https://www.techbargains.com/"),
    ];

    public static DealFeed? ById(string? id) =>
        Feeds.FirstOrDefault(f => f.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string BuildUrl(DealFeed feed, string? query) =>
        feed.UrlTemplate.Replace("{query}", Uri.EscapeDataString(query ?? ""), StringComparison.Ordinal);

    /// <summary>
    /// The page to open when the seller wants to check the scan against the site itself. The
    /// searchable feed's own search page, so "open the search" means the same thing here as it does
    /// for Craigslist.
    /// </summary>
    public static string SearchPageUrl(string? query) =>
        $"https://slickdeals.net/newsearch.php?q={Uri.EscapeDataString(query ?? "")}&searcharea=deals&searchin=first";

    /// <summary>What the results row says was actually searched.</summary>
    public static string ScopeLabel =>
        $"{string.Join(" + ", Feeds.Select(f => f.Site).Distinct())} — online retail, nationwide";
}
