namespace ING_eBay_AutoLister.Services;

/// <summary>What a category typically pays for Promoted Listings, and how crowded that makes it.</summary>
public sealed record CategoryAdRate(string Label, decimal TypicalRatePercent, string Competition, string Basis)
{
    /// <summary>True when a real category matched, rather than the cross-category fallback.</summary>
    public bool Matched => Basis == "matched";
}

/// <summary>
/// Typical Promoted Listings Standard ad rates by category.
/// </summary>
/// <remarks>
/// <para>
/// eBay publishes a "trending ad rate" inside Seller Hub but exposes no API for it — the same wall
/// <see cref="FeeProfile"/> hits on final value fees and <see cref="CrossListingFeeProfile"/> hits on
/// every other marketplace. So these are published/observed typical rates, they are labelled as
/// estimates everywhere they surface, and the seller can override the number with what their own
/// Seller Hub shows.
/// </para>
/// <para>
/// The rate matters for more than the fee: it is the competitive floor. In a category where the
/// field runs 11%, a 2% ad rate buys almost no placement, which is why
/// <see cref="PromotedListingAdvisor"/> uses this figure as the half-saturation point of its lift
/// curve rather than as a recommendation in its own right. Paying the category norm is not the
/// answer — the answer depends on the item's margin, which is exactly what eBay's own suggested
/// rate ignores.
/// </para>
/// </remarks>
public static class PromotedRateNorms
{
    /// <summary>eBay will not carry a Promoted Listings Standard campaign below this rate.</summary>
    public const decimal EbayMinimumRatePercent = 2m;

    /// <summary>
    /// Past this the app stops recommending and starts asking. eBay allows far higher, but a rate
    /// this size is a deliberate clearance decision, not a default a tool should pick for someone.
    /// </summary>
    public const decimal MaxRecommendedRatePercent = 20m;

    /// <summary>Used when nothing matches — roughly eBay's cross-category middle.</summary>
    public const decimal DefaultRatePercent = 7m;

    // Ordered: the first entry whose keywords appear in the category text wins, so the specific
    // ones ("cell phones & accessories") are listed before the generic ones that would also match
    // them ("accessories").
    private static readonly (string Label, string[] Keys, decimal Rate)[] Table =
    [
        ("Cell Phones & Accessories",   ["cell phone", "smartphone", "phone & accessor", "iphone"],                                     7.5m),
        ("Computers & Networking",      ["computer", "tablet", "laptop", "networking", "server", "monitor", "printer"],                 5.5m),
        ("Video Games & Consoles",      ["video game", "console", "playstation", "xbox", "nintendo"],                                   8.0m),
        ("Cameras & Photo",             ["camera", "photo", "lens", "camcorder"],                                                       5.5m),
        ("Consumer Electronics",        ["consumer electronic", "electronics", "tv, video", "audio", "headphone", "speaker", "drone"],  6.0m),
        ("Trading Cards & Sports Mem",  ["trading card", "sports mem", "fan shop", "tcg", "ccg", "graded card"],                        11.0m),
        ("Coins, Stamps & Bullion",     ["coin", "paper money", "stamp", "bullion"],                                                    6.5m),
        ("Collectibles & Antiques",     ["collectib", "antique", "memorabilia", "pottery", "glass", "art"],                             8.0m),
        ("Jewelry & Watches",           ["jewelry", "jewellery", "watch", "necklace", "earring", "gemstone"],                           9.5m),
        ("Clothing, Shoes & Bags",      ["clothing", "shoe", "sneaker", "apparel", "handbag", "purse", "dress", "costume", "boots"],     11.0m),
        ("Health & Beauty",             ["health", "beauty", "fragrance", "makeup", "skin care", "supplement", "vitamin"],              10.0m),
        ("Toys & Hobbies",              ["toy", "hobb", "action figure", "lego", "doll", "model kit", "puzzle"],                         8.5m),
        ("Baby",                        ["baby", "stroller", "nursery", "infant"],                                                       8.5m),
        ("Pet Supplies",                ["pet suppl", "dog suppl", "cat suppl", "aquarium"],                                             8.5m),
        ("Crafts & Sewing",             ["craft", "sewing", "fabric", "scrapbook", "bead", "yarn", "quilt"],                             9.0m),
        ("Musical Instruments",         ["musical instrument", "guitar", "drum", "piano", "pro audio", "synth"],                         6.0m),
        ("Books, Movies & Music",       ["book", "textbook", "magazine", "dvd", "blu-ray", "movie", "vinyl", "cds & vinyl", "music"],    8.0m),
        ("Sporting Goods",              ["sporting good", "fitness", "outdoor", "bicycle", "golf", "hunting", "fishing", "camping"],     7.5m),
        ("Home & Garden",               ["home", "garden", "furniture", "kitchen", "appliance", "decor", "décor", "bedding", "lamp"],    7.5m),
        ("eBay Motors",                 ["motors", "auto part", "car & truck", "motorcycle", "tire", "wheel", "atv", "vehicle"],         4.5m),
        ("Business & Industrial",       ["business", "industrial", "cnc", "test equipment", "heavy equipment", "mining", "hvac"],        4.5m),
        ("Tools & Hardware",            ["tool", "hardware", "drill", "welder", "compressor"],                                          6.5m),
        ("Tickets, Travel & Services",  ["ticket", "travel", "gift card", "specialty service", "coupon"],                               5.0m),
    ];

    /// <summary>
    /// The typical rate for a category, matched on the name eBay reports for the listing.
    /// </summary>
    /// <remarks>
    /// Matching is on the text rather than the category id on purpose: the Trading API returns a
    /// leaf category id (tens of thousands of them, and the tree changes every year), and mapping
    /// leaf to top level would need a live Taxonomy call per listing to answer a question that is
    /// only ever accurate to the nearest percentage point anyway.
    /// </remarks>
    public static CategoryAdRate Resolve(string? categoryName)
    {
        var text = (categoryName ?? "").ToLowerInvariant();
        if (text.Length > 0)
        {
            foreach (var (label, keys, rate) in Table)
            {
                foreach (var key in keys)
                {
                    if (!text.Contains(key, StringComparison.Ordinal)) continue;
                    return new CategoryAdRate(label, rate, CompetitionFor(rate), "matched");
                }
            }
        }

        return new CategoryAdRate("eBay average", DefaultRatePercent, CompetitionFor(DefaultRatePercent), "default");
    }

    /// <summary>The seller's own number, from their Seller Hub. Always beats the published table.</summary>
    public static CategoryAdRate Override(decimal ratePercent, string? categoryName)
    {
        var rate = Math.Clamp(ratePercent, 0.1m, 100m);
        var label = Resolve(categoryName).Label;
        return new CategoryAdRate(label, rate, CompetitionFor(rate), "seller");
    }

    /// <summary>How hard it is to be seen here without paying — the reason the rate is what it is.</summary>
    public static string CompetitionFor(decimal ratePercent) => ratePercent switch
    {
        >= 10m => "very high",
        >= 8m  => "high",
        >= 6m  => "moderate",
        _      => "lower",
    };

    /// <summary>Every category in the table, for the settings screen and the override picker.</summary>
    public static IReadOnlyList<CategoryAdRate> All() =>
        [.. Table.Select(t => new CategoryAdRate(t.Label, t.Rate, CompetitionFor(t.Rate), "matched")),
          new CategoryAdRate("eBay average", DefaultRatePercent, CompetitionFor(DefaultRatePercent), "default")];
}
