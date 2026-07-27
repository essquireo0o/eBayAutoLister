using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Works out what a box will weigh and measure before the seller owns the item.
/// </summary>
/// <remarks>
/// <para>
/// A shipping calculator that demands a weight is useless at the moment it matters most — standing
/// in a garage sale deciding whether $20 is a good price, or looking at a sourcing board of 200
/// items nobody has touched. Every sourcing screen in this app produces titles and categories and
/// no scale readings, so the engine has to be able to answer from a title alone.
/// </para>
/// <para>
/// Matching is keyword-to-package-class, longest keyword first so "gaming laptop" beats "laptop"
/// and "laptop" beats "top". Every estimate carries its basis and is labelled <c>estimated</c> all
/// the way to the screen, so an inferred 4 lb is never displayed as though somebody weighed it.
/// Measured input always wins outright; this only fills a hole.
/// </para>
/// </remarks>
public sealed class PackageEstimator
{
    /// <summary>A packing profile: the shipped box for a class of item, packaging included.</summary>
    private sealed record Profile(string Name, decimal WeightOz, decimal L, decimal W, decimal H, params string[] Keywords);

    /// <summary>
    /// What an unrecognised item is assumed to be.
    /// </summary>
    /// <remarks>
    /// Deliberately a small-parcel-sized box rather than something tiny. An unknown item priced as
    /// an envelope would produce optimistic profit on every board in the app, which is precisely
    /// the failure this whole engine exists to end. When the app has to guess it guesses upward.
    /// </remarks>
    private static readonly Profile Fallback = new("unknown", 32m, 10m, 8m, 4m);

    // Ordered by nothing in particular; matching sorts by keyword length. Weights are the SHIPPED
    // weight — item plus box plus filler — because that is what the label is priced on.
    private static readonly Profile[] Profiles =
    [
        // Flat and light: the only things that can reach the cheap end of the rate card.
        new("trading card",     1m,   6m,  4m, 0.2m, "trading card", "sports card", "pokemon card", "psa", "graded card", "baseball card", "magic the gathering"),
        new("coin",             2m,   6m,  4m, 0.25m, "coin", "silver dollar", "bullion", "numismatic", "banknote", "currency note"),
        new("stamp",            1m,   6m,  4m, 0.15m, "stamp", "postage", "philatelic"),
        new("postcard",         1m,   6m,  4m, 0.15m, "postcard"),
        new("photo",            2m,   7m,  5m, 0.2m, "photograph", "8x10 photo", "signed photo"),

        // Small electronics.
        new("phone",           14m,   8m,  5m,  3m, "iphone", "smartphone", "galaxy s", "pixel phone", "cell phone", "mobile phone"),
        new("tablet",          32m,  12m,  9m,  3m, "ipad", "tablet", "galaxy tab", "kindle"),
        new("smartwatch",      10m,   6m,  5m,  3m, "apple watch", "smartwatch", "fitbit", "garmin watch"),
        new("earbuds",          8m,   7m,  5m,  3m, "airpods", "earbuds", "earphones"),
        new("headphones",      28m,  11m,  9m,  5m, "headphones", "headset"),
        new("camera",          40m,  11m,  9m,  7m, "camera", "dslr", "mirrorless", "camcorder"),
        new("camera lens",     36m,   9m,  7m,  7m, "lens", "telephoto", "zoom lens"),
        new("drone",           64m,  16m, 12m,  7m, "drone", "quadcopter"),
        new("game console",    96m,  18m, 14m,  9m, "playstation", "xbox", "nintendo switch", "game console", "ps5", "ps4"),
        new("video game",       5m,   8m,  6m,  1m, "video game", "game disc", "cartridge"),
        new("controller",      16m,   9m,  7m,  4m, "controller", "gamepad", "joystick"),

        // Computers and components — where dimensional weight and the 70 lb ceiling start to bite.
        new("laptop",          96m,  18m, 14m,  5m, "laptop", "macbook", "notebook computer", "chromebook", "thinkpad"),
        new("desktop pc",     416m,  24m, 20m, 16m, "desktop pc", "gaming pc", "tower pc", "workstation", "computer tower"),
        new("monitor",        224m,  28m, 20m,  8m, "monitor", "lcd display", "computer display"),
        new("television",     640m,  48m, 30m,  8m, "television", "smart tv", "oled tv", "led tv", "flat screen"),
        new("graphics card",   64m,  15m, 10m,  5m, "graphics card", "video card", "rtx", "geforce", "radeon", "gpu"),
        new("motherboard",     48m,  14m, 12m,  4m, "motherboard", "mainboard"),
        new("cpu",              6m,   6m,  5m,  3m, "processor", "cpu", "ryzen", "core i7", "core i9", "xeon"),
        new("ram",              4m,   7m,  5m,  2m, "memory kit", "ddr4", "ddr5", "ram module", "sodimm"),
        new("hard drive",      16m,   8m,  6m,  3m, "hard drive", "hdd", "ssd", "solid state drive", "nvme"),
        new("power supply",    80m,  12m,  9m,  7m, "power supply", "psu", "atx power"),
        new("keyboard",        48m,  20m,  9m,  3m, "keyboard"),
        new("mouse",           12m,   8m,  6m,  3m, "mouse", "trackball"),
        new("router",          32m,  12m, 10m,  5m, "router", "modem", "access point", "switch 8-port"),
        new("printer",        320m,  22m, 18m, 14m, "printer", "inkjet", "laserjet", "scanner"),

        // Mining hardware — this app's own sourcing screens surface these constantly, and every one
        // of them is over the USPS weight ceiling. Getting them wrong is getting them very wrong.
        new("asic miner",     704m,  22m, 16m, 14m, "antminer", "whatsminer", "asic miner", "bitcoin miner", "avalonminer", "goldshell", "iceriver"),
        new("miner psu",      288m,  16m, 12m, 10m, "apw3", "apw12", "miner power supply", "server psu"),

        // Tools and outdoor: heavy, awkward, and where flat-rate boxes win big.
        new("power tool",     112m,  16m, 12m,  8m, "drill", "impact driver", "circular saw", "power tool", "angle grinder", "sander", "nail gun"),
        new("hand tool",       32m,  12m,  8m,  4m, "wrench", "socket set", "screwdriver set", "pliers", "hand tool"),
        new("tool battery",    32m,   8m,  6m,  5m, "tool battery", "18v battery", "20v max battery", "m18", "dewalt battery"),
        new("chainsaw",       256m,  26m, 12m, 12m, "chainsaw"),
        new("lawn mower",    1200m,  32m, 24m, 20m, "lawn mower", "leaf blower", "string trimmer"),

        // Media, apparel, and the long tail.
        new("book",            24m,  10m,  8m,  2m, "book", "hardcover", "paperback", "textbook"),
        new("vinyl record",    12m,  13m, 13m,  1m, "vinyl", "lp record", "45 rpm"),
        new("dvd",              6m,   8m,  6m,  1m, "dvd", "blu-ray", "boxed set"),
        new("clothing",        14m,  12m, 10m,  3m, "shirt", "jacket", "hoodie", "jeans", "dress", "sweater", "coat"),
        new("shoes",           40m,  14m, 10m,  6m, "shoes", "sneakers", "boots", "trainers"),
        new("handbag",         32m,  14m, 11m,  6m, "handbag", "purse", "backpack", "tote bag"),
        new("jewelry",          4m,   6m,  5m,  2m, "ring", "necklace", "bracelet", "earrings", "jewelry"),
        new("watch",           10m,   7m,  5m,  4m, "wristwatch", "watch"),
        new("toy",             24m,  12m,  9m,  6m, "action figure", "lego", "doll", "toy", "figurine"),
        new("kitchen appliance", 160m, 16m, 14m, 12m, "blender", "mixer", "air fryer", "coffee maker", "toaster", "instant pot"),
        new("vacuum",         240m,  20m, 14m, 12m, "vacuum", "dyson"),
        new("guitar",         320m,  46m, 18m,  7m, "guitar", "bass guitar", "ukulele"),
        new("amplifier",      384m,  22m, 18m, 12m, "amplifier", "receiver", "amp head", "stereo receiver"),
        new("speaker",        224m,  18m, 14m, 12m, "speaker", "subwoofer", "soundbar"),
        new("auto part",      160m,  18m, 14m, 10m, "alternator", "starter motor", "brake caliper", "cylinder head", "auto part"),
        new("tire",           400m,  27m, 27m, 10m, "tire", "tyre"),
        new("furniture",      800m,  36m, 24m, 20m, "chair", "table", "desk", "dresser", "bookcase"),
    ];

    // Flattened once: (keyword, profile), longest keyword first, so the most specific match wins.
    private static readonly (string Keyword, Profile Profile)[] Index =
        Profiles
            .SelectMany(p => p.Keywords.Select(k => (Keyword: k.ToLowerInvariant(), Profile: p)))
            .Where(t => t.Keyword.Length > 0)
            .OrderByDescending(t => t.Keyword.Length)
            .ToArray();

    /// <summary>
    /// The package for an item, preferring anything the seller actually measured.
    /// </summary>
    /// <param name="weightOz">
    /// Total measured weight in ounces. Zero or negative means "not measured" — which is different
    /// from "weighs nothing", and is why this is not merged with the estimate.
    /// </param>
    public PackageSpec Estimate(
        string? title, string? category = null,
        decimal weightOz = 0m, decimal lengthIn = 0m, decimal widthIn = 0m, decimal heightIn = 0m)
    {
        var matched = Match(title, category);
        var hasWeight = weightOz > 0m;
        var hasDims = lengthIn > 0m && widthIn > 0m && heightIn > 0m;

        var spec = new PackageSpec
        {
            WeightOz = hasWeight ? Math.Round(weightOz, 2) : matched.Profile.WeightOz,
            LengthIn = hasDims ? lengthIn : matched.Profile.L,
            WidthIn = hasDims ? widthIn : matched.Profile.W,
            HeightIn = hasDims ? heightIn : matched.Profile.H,
            Profile = matched.Profile.Name,
        };

        spec.Source = (hasWeight, hasDims) switch
        {
            (true, true) => "measured",
            (false, false) when matched.Keyword is null => "fallback",
            _ => "estimated",
        };

        spec.Basis = Describe(spec.Source, matched.Keyword, hasWeight, hasDims, matched.Profile);
        return spec;
    }

    /// <summary>Convenience overload for the listing editor, which carries pounds and ounces apart.</summary>
    public PackageSpec EstimateFromListing(ListingData listing) => Estimate(
        listing.Title, listing.Category,
        weightOz: listing.WeightLbs * 16m + listing.WeightOz,
        lengthIn: listing.PackageLengthIn, widthIn: listing.PackageWidthIn, heightIn: listing.PackageHeightIn);

    private static (Profile Profile, string? Keyword) Match(string? title, string? category)
    {
        var haystack = ((title ?? "") + " " + (category ?? "")).ToLowerInvariant();
        if (haystack.Trim().Length == 0) return (Fallback, null);

        foreach (var (keyword, profile) in Index)
            if (haystack.Contains(keyword, StringComparison.Ordinal))
                return (profile, keyword);

        return (Fallback, null);
    }

    private static string Describe(string source, string? keyword, bool hasWeight, bool hasDims, Profile profile) => source switch
    {
        "measured" => "Your measurements.",
        "fallback" => "Nothing in the title said what this is, so it is priced as a small parcel — "
                    + "enter a weight to replace the guess.",
        _ when hasWeight && !hasDims =>
            $"Your weight, with a typical \"{profile.Name}\" box for the dimensions"
            + (keyword is null ? "." : $" (matched \"{keyword}\")."),
        _ when !hasWeight && hasDims =>
            $"Your dimensions, with a typical \"{profile.Name}\" weight"
            + (keyword is null ? "." : $" (matched \"{keyword}\")."),
        _ => $"Estimated from \"{keyword}\" in the title — a typical packed {profile.Name}. "
           + "Weigh it to make this exact.",
    };
}
