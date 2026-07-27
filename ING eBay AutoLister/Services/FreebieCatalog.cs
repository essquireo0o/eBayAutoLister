namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Where free supply is, and the only file to edit when one of these moves.
///
/// Two very different kinds of source, listed together because they answer the same question:
///
///   • <b>The local free board.</b> Craigslist's free-stuff category is the best free supply that
///     exists — a live pull of one metro this session returned 186 posts including a 65" LG smart
///     TV, a Craftsman tool chest, a DeWalt circular saw, a Yamaha grand piano, a Proform treadmill
///     and a Samsung washer/dryer set. All at $0, all collected in person.
///   • <b>Free-after-coupon and free-after-rebate deals.</b> Nationwide, shipped, and priced by
///     <see cref="FreebiePricer"/> rather than assumed to cost nothing, because they don't.
///
/// Posture, identical to every other public source in this app: user-driven only, one request per
/// slice per click, nothing scheduled, nothing crawled, nothing stored, no account, no key.
/// </summary>
public static class FreebieCatalog
{
    /// <summary>The id the source picker and the <c>sources=</c> parameter use.</summary>
    public const string SourceId = "freebies";

    public const string SourceLabel = "Free & free-after-coupon";

    /// <summary>
    /// Craigslist's own category code for the free-stuff board. Everything on it is $0 by
    /// construction, which is why <see cref="FreebieClassifier"/> doesn't require these posts to say
    /// the word: "Oak wall unit" on the free board is a free oak wall unit.
    /// </summary>
    public const string CraigslistFreeCategory = "zip";

    /// <summary>
    /// The deal-feed slices, reusing <see cref="DealFeed"/> so the RSS reading, the block detection
    /// and the cross-feed dedupe are the ones the Deal Scanner already uses rather than a second
    /// copy of them.
    /// </summary>
    /// <remarks>
    /// Only the first carries <c>{query}</c>, and that distinction is doing real work: a slice the
    /// site ran for the seller's own words earns the lenient relevance filter
    /// (<see cref="LocalSupplyResults.FilterByRelevance"/>), while the fixed slices are a firehose
    /// nobody asked about and are matched strictly against the query — exactly the split
    /// <see cref="DealFeedParser.BuildResult"/> already makes.
    /// </remarks>
    public static readonly IReadOnlyList<DealFeed> Feeds =
    [
        // The seller's own words, plus the one word that makes the search this board's rather than
        // the Deal Scanner's. Slickdeals honours ?q= server-side.
        new("slickdeals-free-query", "Slickdeals", "Slickdeals — your search, free items",
            "https://slickdeals.net/newsearch.php?q={query}+free&searcharea=deals&searchin=first&rss=1",
            "https://slickdeals.net/"),

        // Free-after-rebate is where the genuinely valuable freebies are: real hardware, at $0 net,
        // for anybody willing to post a form. Priced honestly by FreebiePricer, which refuses to
        // pretend the money isn't fronted.
        new("slickdeals-rebate", "Slickdeals", "Slickdeals — free after rebate",
            "https://slickdeals.net/newsearch.php?q=free+after+rebate&searcharea=deals&searchin=first&rss=1",
            "https://slickdeals.net/"),

        new("slickdeals-coupon", "Slickdeals", "Slickdeals — free after coupon",
            "https://slickdeals.net/newsearch.php?q=free+after+coupon&searcharea=deals&searchin=first&rss=1",
            "https://slickdeals.net/"),

        // The community's own word for it, which surfaces threads the phrasings above miss.
        new("slickdeals-freebies", "Slickdeals", "Slickdeals — freebies",
            "https://slickdeals.net/newsearch.php?q=freebie&searcharea=deals&searchin=first&rss=1",
            "https://slickdeals.net/"),

        // The arithmetic form. Catches the entries written as a discount rather than as a giveaway.
        new("slickdeals-100off", "Slickdeals", "Slickdeals — 100% off",
            "https://slickdeals.net/newsearch.php?q=100%25+off&searcharea=deals&searchin=first&rss=1",
            "https://slickdeals.net/"),
    ];

    /// <summary>
    /// Free supply worth having that this app deliberately does not read, offered as a prefilled
    /// link instead. Both are membership networks organised around real local groups — there is no
    /// public search to call, and pretending otherwise would ship a source chip that is red every
    /// time. The same answer <see cref="LiquidationCatalog"/> gives for the sites that block robots.
    /// </summary>
    public static readonly IReadOnlyList<(string Id, string Label, string UrlTemplate, string Note)> ManualSites =
    [
        ("freecycle", "Freecycle", "https://www.freecycle.org/",
            "Local giving-away groups. Free membership, then browse your own town's board — no public search to read."),
        ("buynothing", "Buy Nothing", "https://buynothingproject.org/find-a-group",
            "Neighbourhood gift groups, mostly run on Facebook. Find your local one, then watch it — this is where the good free furniture goes before it ever reaches a classifieds board."),
    ];

    public static string BuildUrl(DealFeed feed, string? query) =>
        feed.UrlTemplate.Replace("{query}", Uri.EscapeDataString(query ?? ""), StringComparison.Ordinal);

    /// <summary>The page to open to check a scan against the site itself.</summary>
    public static string SearchPageUrl(string? query) =>
        $"https://slickdeals.net/newsearch.php?q={Uri.EscapeDataString($"{query} free".Trim())}&searcharea=deals&searchin=first";

    /// <summary>
    /// What the results row says was actually searched. Named per leg because they are not the same
    /// promise: one is a board in the seller's own metro, the other is the whole country.
    /// </summary>
    public static string ScopeLabel(string? craigslistSiteLabel) =>
        craigslistSiteLabel is { Length: > 0 }
            ? $"{craigslistSiteLabel} craigslist free stuff + Slickdeals freebies nationwide"
            : "Slickdeals freebies — nationwide";
}
