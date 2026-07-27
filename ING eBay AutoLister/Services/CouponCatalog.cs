using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// One store, as coupon lists name it. <paramref name="Aliases"/> is what the deal feeds actually
/// print — a retailer arrives as "Home Depot", "homedepot.com", "The Home Depot" or "HomeDepot"
/// depending on which aggregator published it, and all four have to reach the same code list.
/// </summary>
/// <param name="CodesRare">
/// True for the stores where a promo code is the wrong thing to look for. Said out loud rather than
/// left as an empty result, because "no codes found at Amazon" reads as "no discount available"
/// when the truth is that Amazon's discounts live somewhere a code list cannot see.
/// </param>
public sealed record CouponMerchant(
    string Id, string Label, string Domain, string[] Aliases, bool CodesRare = false, string Note = "")
{
    /// <summary>False on a store this app doesn't catalogue — see <see cref="CouponCatalog.Resolve"/>.</summary>
    public bool Known { get; init; } = true;
}

/// <summary>One public coupon list. A <c>{store}</c> placeholder means the site runs the search itself.</summary>
public sealed record CouponFeed(string Id, string Site, string Label, string UrlTemplate)
{
    public bool IsStoreSearch => UrlTemplate.Contains("{store}", StringComparison.Ordinal);
}

/// <summary>
/// Every coupon URL and every store name in one place, and the only file to edit when one of them
/// moves — the same posture as <see cref="DealFeedCatalog"/>, which owns the deal feeds this sits
/// beside.
/// </summary>
/// <remarks>
/// <para>
/// Two kinds of list exist and they fail in opposite directions, so both are here:
/// </para>
/// <list type="bullet">
///   <item><b>Readable.</b> The deal aggregators publish their coupon threads as the same public RSS
///   this app already reads for clearance. Machine-readable, no account, one GET.</item>
///   <item><b>Not readable.</b> RetailMeNot, Coupons.com and the cashback portals answer an
///   automated request with a block page. Pretending otherwise would put a permanently red source
///   chip in front of the seller, so those are offered as prefilled links instead — see
///   <see cref="ManualSitesFor"/>. The seller opening RetailMeNot themselves is the whole feature
///   working, not a fallback.</item>
/// </list>
/// </remarks>
public static class CouponCatalog
{
    /// <summary>
    /// Coupon lists that can actually be read. Ordered by how store-specific each answer is: a
    /// search for the seller's store first, the general firehose last, because the lookup budget is
    /// spent in this order.
    /// </summary>
    public static readonly IReadOnlyList<CouponFeed> Feeds =
    [
        // The store's own name plus the words that make an entry a coupon entry. Slickdeals honours
        // ?q= server-side, which is what makes a per-store lookup one request rather than a crawl.
        new("slickdeals-codes", "Slickdeals", "Slickdeals promo codes",
            "https://slickdeals.net/newsearch.php?q={store}+promo+code&searcharea=deals&searchin=first&rss=1"),

        // Coupon threads specifically. Overlaps the above deliberately — the two phrasings surface
        // different threads, and a duplicate code costs nothing (they are deduped by code).
        new("slickdeals-coupons", "Slickdeals", "Slickdeals coupons",
            "https://slickdeals.net/newsearch.php?q={store}+coupon&searcharea=deals&searchin=first&rss=1"),

        // DealNews is editorially curated, which on coupons matters more than anywhere else: an
        // editor checked that the code existed at the time of writing.
        //
        // Its front page rather than a store search, because DealNews publishes no working search
        // feed — every documented form of one answers 204 or 301, verified against the live site.
        // A browse feed costs nothing extra here: CouponParser drops every entry that isn't about
        // the store being looked up, which is the same filter the deal-feed firehoses get.
        new("dealnews-coupons", "DealNews", "DealNews coupons",
            "https://www.dealnews.com/?rss=1"),
    ];

    /// <summary>
    /// The stores worth catalogueing: the ones a resale seller actually sources from, and the ones
    /// the deal feeds name most. An uncatalogued store still gets searched under its own name —
    /// see <see cref="Resolve"/> — this list only buys better matching and the store-specific links.
    /// </summary>
    public static readonly IReadOnlyList<CouponMerchant> Merchants =
    [
        new("amazon", "Amazon", "amazon.com", ["amazon.com", "amzn", "woot"], CodesRare: true,
            Note: "Amazon almost never takes a typed code — its discounts are the clip-the-coupon box on the " +
                  "item page, a lightning deal, or Subscribe & Save. Check the item page itself before assuming there's nothing."),
        new("walmart", "Walmart", "walmart.com", ["walmart.com", "wal-mart", "wal mart"], CodesRare: true,
            Note: "Walmart rarely honours store-wide codes. The saving is usually the rollback price itself, plus free pickup instead of shipping."),
        new("costco", "Costco", "costco.com", ["costco.com", "costco wholesale"], CodesRare: true,
            Note: "Costco discounts are the member-only warehouse offers in their monthly book, not codes — there is nothing to type."),
        new("target", "Target", "target.com", ["target.com"]),
        new("bestbuy", "Best Buy", "bestbuy.com", ["bestbuy.com", "best buy", "bestbuy"]),
        new("homedepot", "The Home Depot", "homedepot.com", ["homedepot.com", "home depot", "the home depot"]),
        new("lowes", "Lowe's", "lowes.com", ["lowes.com", "lowe's", "lowes"]),
        new("newegg", "Newegg", "newegg.com", ["newegg.com", "newegg.ca"]),
        new("bhphoto", "B&H Photo", "bhphotovideo.com", ["bhphotovideo.com", "b&h", "b and h", "bh photo"]),
        new("adorama", "Adorama", "adorama.com", ["adorama.com"]),
        new("microcenter", "Micro Center", "microcenter.com", ["microcenter.com", "micro center"]),
        new("dell", "Dell", "dell.com", ["dell.com", "dell home", "alienware"]),
        new("hp", "HP", "hp.com", ["hp.com", "hewlett packard", "hewlett-packard"]),
        new("lenovo", "Lenovo", "lenovo.com", ["lenovo.com"]),
        new("staples", "Staples", "staples.com", ["staples.com"]),
        new("officedepot", "Office Depot", "officedepot.com", ["officedepot.com", "office depot", "officemax", "office max"]),
        new("gamestop", "GameStop", "gamestop.com", ["gamestop.com", "game stop"]),
        new("ebay", "eBay", "ebay.com", ["ebay.com"]),
        new("kohls", "Kohl's", "kohls.com", ["kohls.com", "kohl's", "kohls"]),
        new("macys", "Macy's", "macys.com", ["macys.com", "macy's", "macys"]),
        new("jcpenney", "JCPenney", "jcpenney.com", ["jcpenney.com", "jc penney", "jcp"]),
        new("overstock", "Overstock", "overstock.com", ["overstock.com", "bed bath & beyond", "bedbathandbeyond.com"]),
        new("wayfair", "Wayfair", "wayfair.com", ["wayfair.com"]),
        new("harborfreight", "Harbor Freight", "harborfreight.com", ["harborfreight.com", "harbor freight"]),
        new("acehardware", "Ace Hardware", "acehardware.com", ["acehardware.com", "ace hardware"]),
        new("autozone", "AutoZone", "autozone.com", ["autozone.com", "auto zone"]),
        new("dickssportinggoods", "Dick's Sporting Goods", "dickssportinggoods.com",
            ["dickssportinggoods.com", "dick's sporting goods", "dicks sporting goods"]),
        new("rei", "REI", "rei.com", ["rei.com", "rei co-op"]),
        new("nike", "Nike", "nike.com", ["nike.com"]),
        new("samsung", "Samsung", "samsung.com", ["samsung.com"]),
        new("gap", "Gap", "gap.com", ["gap.com", "old navy", "oldnavy.com", "banana republic"]),
        new("sephora", "Sephora", "sephora.com", ["sephora.com"]),
        new("ulta", "Ulta Beauty", "ulta.com", ["ulta.com", "ulta beauty"]),
        new("petco", "Petco", "petco.com", ["petco.com"]),
        new("chewy", "Chewy", "chewy.com", ["chewy.com"]),
    ];

    /// <summary>
    /// The store a deal was bought from, matched to a catalogued one.
    /// </summary>
    /// <remarks>
    /// Never returns null on a non-empty name. An uncatalogued store is returned as an unknown
    /// merchant carrying the name as given, because the readable lists are keyword searches and a
    /// store nobody thought to catalogue still has codes published for it. What an unknown merchant
    /// loses is only the store-specific links and the "this store doesn't do codes" note.
    /// </remarks>
    public static CouponMerchant? Resolve(string? retailer)
    {
        var raw = (retailer ?? "").Trim();
        if (raw.Length == 0) return null;

        var needle = Normalize(raw);
        if (needle.Length == 0) return null;

        foreach (var merchant in Merchants)
        {
            if (Normalize(merchant.Id) == needle || Normalize(merchant.Label) == needle
                || Normalize(merchant.Domain) == needle
                || merchant.Aliases.Any(a => Normalize(a) == needle))
                return merchant;
        }

        // A domain the catalogue doesn't carry ("monoprice.com") is still a store name; strip the
        // suffix so the keyword search is run on something a coupon list would print.
        var label = raw;
        var dot = label.IndexOf('.');
        if (dot > 1 && !label.Contains(' ')) label = label[..dot];

        return new CouponMerchant(needle, Titleize(label), raw.Contains('.') ? raw.ToLowerInvariant() : "", [])
        {
            Known = false,
        };
    }

    public static string BuildUrl(CouponFeed feed, string store) =>
        feed.UrlTemplate.Replace("{store}", Uri.EscapeDataString(store ?? ""), StringComparison.Ordinal);

    /// <summary>
    /// The lists that refuse to be read by a program, as one-click searches for this store.
    /// </summary>
    /// <remarks>
    /// The cashback portals are here for a second reason as well as the block page: their rates are
    /// account-specific and change daily, so a rate this app read an hour ago and printed as money
    /// would be a number the seller cannot hold anyone to. A link to the live rate is the honest
    /// version of that, and the seller is one click from the real figure.
    /// </remarks>
    public static List<LocalSupplyManualSite> ManualSitesFor(CouponMerchant merchant)
    {
        var query = merchant.Domain.Length > 0 ? merchant.Domain : merchant.Label;

        LocalSupplyManualSite[] sites =
        [
            new LocalSupplyManualSite
            {
                Id = "retailmenot", Label = "RetailMeNot",
                UrlTemplate = "https://www.retailmenot.com/search?q={query}",
                Note = "The biggest public code list. Blocks automated reads, so open it yourself — it takes ten seconds.",
            },
            new LocalSupplyManualSite
            {
                Id = "coupons", Label = "Coupons.com",
                UrlTemplate = "https://www.coupons.com/search/?q={query}",
                Note = "Second opinion on the same codes, and better on the grocery and household stores.",
            },
            new LocalSupplyManualSite
            {
                Id = "rakuten", Label = "Rakuten (cash back)",
                UrlTemplate = "https://www.rakuten.com/search?query={query}",
                Note = "Stacks with a code — it pays a percentage back after the order rather than at the till. Rates change daily, so check the live one.",
            },
            new LocalSupplyManualSite
            {
                Id = "topcashback", Label = "TopCashback",
                UrlTemplate = "https://www.topcashback.com/search/?q={query}",
                Note = "Usually the higher rate of the two portals. Same deal: paid weeks later, so treat it as a rebate, not a discount.",
            },
        ];

        // The {query} placeholder is filled in here rather than in the browser: these are the
        // seller's own one-click searches, and the store name is a server-side fact.
        foreach (var site in sites)
            site.UrlTemplate = site.UrlTemplate.Replace("{query}", Uri.EscapeDataString(query), StringComparison.Ordinal);

        return [.. sites];
    }

    /// <summary>Lower-case, letters and digits only — "The Home Depot", "homedepot.com" and "HomeDepot" all meet here.</summary>
    public static string Normalize(string? value)
    {
        var text = (value ?? "").ToLowerInvariant();
        // The domain suffix is noise for matching and nothing else; "amazon.com" is "amazon".
        foreach (var suffix in Suffixes)
            if (text.EndsWith(suffix, StringComparison.Ordinal)) { text = text[..^suffix.Length]; break; }

        return new string(text.Where(char.IsLetterOrDigit).ToArray());
    }

    private static readonly string[] Suffixes = [".com", ".net", ".org", ".us", ".co", ".ca"];

    private static string Titleize(string value) =>
        string.Join(' ', value.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Length <= 1 ? word.ToUpperInvariant() : char.ToUpperInvariant(word[0]) + word[1..]));
}
