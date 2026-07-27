using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>How the thing gets from the seller to the buyer, and therefore what the sale costs.</summary>
public enum SaleChannel
{
    /// <summary>Boxed and posted. eBay's percentage final value fee, a shipping label, packaging. The default, and every flip this app priced until categories existed.</summary>
    EbayParcel,
    /// <summary>Sold on eBay, collected in person. The percentage fee still applies; there is no label, no box and no shipping cost — a sofa is not a parcel.</summary>
    EbayLocalPickup,
    /// <summary>Sold through eBay Motors and driven or towed away. A flat vehicle fee instead of the percentage one, no shipping at all, and a title to transfer.</summary>
    EbayMotors,
}

/// <summary>
/// One kind of thing to flip, and the three answers that differ per kind: which classifieds board
/// it lives on, what values it, and what selling it costs.
/// </summary>
public sealed class ResaleCategory
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    /// <summary>The picker heading: Anything | Vehicles | Big items | Small items.</summary>
    public required string Group { get; init; }

    /// <summary>The craigslist board code this category searches — see CraigslistParser.BuildSearchUrl.</summary>
    public string CraigslistBoard { get; init; } = CraigslistParser.ForSaleCategory;

    public SaleChannel Channel { get; init; } = SaleChannel.EbayParcel;

    /// <summary>Which valuation provider owns this category — see Services/ResaleValuation.cs.</summary>
    public string ValuationProviderId { get; init; } = ResaleValuationProviders.EbayComps;

    /// <summary>
    /// The flat marketplace fee on a vehicle sale, in dollars. Zero on every percentage-fee
    /// category. An estimate exactly like the 13.25% final value fee is — see
    /// <see cref="CategoryCosts"/> for why it isn't on FeeProfile.
    /// </summary>
    public decimal FlatSaleFee { get; init; }

    /// <summary>True when there is a title to sign over, which costs money and paperwork.</summary>
    public bool IsTitled { get; init; }

    /// <summary>True for the categories a vehicle parse applies to — see VehicleTitleParser.</summary>
    public bool IsVehicle { get; init; }

    /// <summary>
    /// Words and phrases that identify this category in a listing title, checked in catalog order
    /// and matched on whole words — see <see cref="VehicleTitleParser.ContainsWord"/>, without which
    /// "cargo trailer" would be a car and "dishwasher" an item of laundry.
    /// </summary>
    public string[] Keywords { get; init; } = [];

    /// <summary>
    /// Phrases that veto this category however well its own keywords matched. Exists for exactly one
    /// class of collision: a pressure washer is a tool, and it is spelled with the word this app uses
    /// to recognise a laundry appliance.
    /// </summary>
    public string[] ExcludeKeywords { get; init; } = [];

    /// <summary>eBay's own category id, for the prefilled sold-listings link. Empty means site-wide.</summary>
    public string EbaySoldCategoryId { get; init; } = "";

    /// <summary>One line under the picker saying what changes when this is chosen.</summary>
    public string Hint { get; init; } = "";

    public bool IsDefault => Id == ResaleCategoryCatalog.AnythingId;

    /// <summary>True when this app has no comp source it trusts for the category and will say so per row.</summary>
    public bool ValuedByHand => ValuationProviderId != ResaleValuationProviders.EbayComps;
}

/// <summary>
/// The categories the Deal Scanner can be pointed at, and the one job nothing else can do:
/// deciding which one a listing belongs to.
/// </summary>
/// <remarks>
/// <para>
/// Classification is deliberately kept at the pipeline edge rather than inside
/// <see cref="LocalArbitrageAnalyzer"/>. The analyzer prices a row against the category it was
/// handed; the sources and the scan decide what that category is. That split is what lets a
/// craigslist search of the cars board state the category as fact — it searched the cars board —
/// while a keyword-only source has to earn it from the title.
/// </para>
/// <para>
/// Everything here is pure, static and total. A title nothing matches is
/// <see cref="AnythingId"/>, which is exactly the behaviour the board had before categories
/// existed: parcel fees, parcel shipping, eBay sold comps.
/// </para>
/// </remarks>
public static class ResaleCategoryCatalog
{
    public const string AnythingId = "anything";
    public const string CarsId = "cars";
    public const string PowersportsId = "powersports";
    public const string BoatsId = "boats";
    public const string RvsId = "rvs";
    public const string TrailersId = "trailers";
    public const string TractorsId = "tractors";
    public const string AppliancesId = "appliances";
    public const string FurnitureId = "furniture";
    public const string ToolsId = "tools";
    public const string ElectronicsId = "electronics";

    /// <summary>eBay Motors' own category root, for the prefilled sold-listings search.</summary>
    private const string MotorsCategoryId = "6001";

    // Order matters twice over: it is the order the picker renders in, and it is the order
    // keyword detection runs in. "Travel trailer" has to reach RVs before Trailers, and "pressure
    // washer" has to reach Tools before Appliances — both are handled by sitting earlier in this
    // list, which is cheaper to read and to change than a pile of negative lookarounds.
    public static readonly IReadOnlyList<ResaleCategory> All =
    [
        new()
        {
            Id = AnythingId, Label = "Anything", Group = "Anything",
            CraigslistBoard = CraigslistParser.ForSaleCategory,
            Hint = "Searches the whole for-sale board and works the category out per listing.",
        },

        // ── Vehicles: the big local money, and the three things the parcel model gets wrong ──────
        new()
        {
            Id = CarsId, Label = "Cars & trucks", Group = "Vehicles",
            CraigslistBoard = "cta", Channel = SaleChannel.EbayMotors,
            ValuationProviderId = ResaleValuationProviders.EbayMotors,
            FlatSaleFee = CategoryCosts.VehicleSaleFee, IsTitled = true, IsVehicle = true,
            EbaySoldCategoryId = MotorsCategoryId,
            Keywords = ["pickup truck", "crew cab", "sedan", "minivan", "suv", "hatchback", "convertible",
                        "coupe", "station wagon", "4x4", "awd", "diesel truck", "pickup", "truck", "car"],
            Hint = "Craigslist cars & trucks board. No shipping, flat eBay Motors fee, title transfer costed.",
        },
        new()
        {
            Id = RvsId, Label = "RVs & campers", Group = "Vehicles",
            CraigslistBoard = "rva", Channel = SaleChannel.EbayMotors,
            ValuationProviderId = ResaleValuationProviders.EbayMotors,
            FlatSaleFee = CategoryCosts.VehicleSaleFee, IsTitled = true, IsVehicle = true,
            EbaySoldCategoryId = MotorsCategoryId,
            Keywords = ["travel trailer", "fifth wheel", "5th wheel", "toy hauler", "motorhome", "motor home",
                        "class a", "class c", "pop up camper", "popup camper", "teardrop", "camper", "rv"],
            Hint = "Craigslist RV board. Collected, not shipped — and travel trailers read as RVs, not trailers.",
        },
        new()
        {
            Id = BoatsId, Label = "Boats", Group = "Vehicles",
            CraigslistBoard = "boo", Channel = SaleChannel.EbayMotors,
            ValuationProviderId = ResaleValuationProviders.EbayMotors,
            FlatSaleFee = CategoryCosts.PowersportsSaleFee, IsTitled = true, IsVehicle = true,
            EbaySoldCategoryId = MotorsCategoryId,
            Keywords = ["pontoon", "sailboat", "jon boat", "bass boat", "deck boat", "center console",
                        "cabin cruiser", "catamaran", "outboard", "yacht", "skiff", "boat"],
            Hint = "Craigslist boats board. Hours rather than miles, and a trailer usually comes with it.",
        },
        new()
        {
            Id = PowersportsId, Label = "Motorcycles & powersports", Group = "Vehicles",
            CraigslistBoard = "mca", Channel = SaleChannel.EbayMotors,
            ValuationProviderId = ResaleValuationProviders.EbayMotors,
            FlatSaleFee = CategoryCosts.PowersportsSaleFee, IsTitled = true, IsVehicle = true,
            EbaySoldCategoryId = MotorsCategoryId,
            Keywords = ["motorcycle", "dirt bike", "dirtbike", "side by side", "side-by-side", "four wheeler",
                        "4 wheeler", "snowmobile", "jet ski", "jetski", "waverunner", "wave runner", "golf cart",
                        "scooter", "moped", "atv", "utv", "quad"],
            Hint = "Craigslist motorcycles board. Bikes, quads, side-by-sides and skis — all collected in person.",
        },
        new()
        {
            Id = TrailersId, Label = "Trailers", Group = "Vehicles",
            CraigslistBoard = "tra", Channel = SaleChannel.EbayMotors,
            ValuationProviderId = ResaleValuationProviders.EbayMotors,
            FlatSaleFee = CategoryCosts.PowersportsSaleFee, IsTitled = true, IsVehicle = true,
            EbaySoldCategoryId = MotorsCategoryId,
            Keywords = ["utility trailer", "dump trailer", "enclosed trailer", "car hauler", "flatbed trailer",
                        "gooseneck", "equipment trailer", "cargo trailer", "trailer"],
            Hint = "Craigslist trailers board. Towed away, titled in most states, no shipping.",
        },
        new()
        {
            Id = TractorsId, Label = "Tractors & heavy equipment", Group = "Vehicles",
            CraigslistBoard = "hva", Channel = SaleChannel.EbayLocalPickup,
            ValuationProviderId = ResaleValuationProviders.BulkyLocal,
            IsVehicle = true,
            Keywords = ["skid steer", "skidsteer", "mini excavator", "excavator", "backhoe", "bulldozer",
                        "front loader", "telehandler", "forklift", "zero turn", "riding mower", "combine",
                        "tractor"],
            Hint = "Craigslist heavy equipment board. Hours, not miles — and it needs a trailer to move.",
        },

        // ── Big items: sold on eBay, but nobody posts a fridge ───────────────────────────────────
        new()
        {
            Id = AppliancesId, Label = "Appliances", Group = "Big items",
            CraigslistBoard = "ppa", Channel = SaleChannel.EbayLocalPickup,
            ValuationProviderId = ResaleValuationProviders.BulkyLocal,
            Keywords = ["refrigerator", "fridge", "freezer", "dishwasher", "washer and dryer", "washer/dryer",
                        "clothes dryer", "water heater", "wine cooler", "kegerator", "cooktop", "range hood",
                        "wall oven", "stove", "oven", "washer", "dryer"],
            // The one real collision in the catalog: a pressure washer is a tool, and Appliances sits
            // ahead of Tools in the picker's own ordering.
            ExcludeKeywords = ["pressure washer", "power washer"],
            Hint = "Craigslist appliances board. Collected, so no shipping is charged against the profit.",
        },
        new()
        {
            Id = FurnitureId, Label = "Furniture", Group = "Big items",
            CraigslistBoard = "fua", Channel = SaleChannel.EbayLocalPickup,
            ValuationProviderId = ResaleValuationProviders.BulkyLocal,
            Keywords = ["sectional", "sofa", "couch", "loveseat", "recliner", "dresser", "armoire", "wardrobe",
                        "bed frame", "headboard", "mattress", "dining table", "dining set", "china cabinet",
                        "bookcase", "bookshelf", "nightstand", "patio set", "credenza", "buffet hutch",
                        // Qualified on purpose: a bare "desk" is also the first word of "desk lamp",
                        // "desk fan" and "desk chair mat", all of which post in a box.
                        "office desk", "writing desk", "computer desk", "standing desk", "executive desk"],
            Hint = "Craigslist furniture board. Local pickup — and eBay sold comps rarely mean anything here.",
        },

        // ── Small items: the parcel model, unchanged ─────────────────────────────────────────────
        new()
        {
            Id = ToolsId, Label = "Tools & equipment", Group = "Small items",
            CraigslistBoard = "tla",
            Keywords = ["pressure washer", "power washer", "impact wrench", "air compressor", "table saw",
                        "miter saw", "circular saw", "chainsaw", "nail gun", "welder", "generator", "socket set",
                        "tool set", "tool box", "drill", "sander", "grinder", "lathe", "leaf blower", "lawn mower"],
            Hint = "Craigslist tools board. Priced and costed exactly like the rest of the board.",
        },
        new()
        {
            Id = ElectronicsId, Label = "Electronics & computers", Group = "Small items",
            CraigslistBoard = "ela",
            Keywords = ["macbook", "laptop", "iphone", "ipad", "graphics card", "video card", "motherboard",
                        "antminer", "asic miner", "playstation", "ps5", "xbox", "nintendo", "monitor",
                        "television", "camera", "drone", "headphones", "soundbar", "tablet", "console"],
            Hint = "Craigslist electronics board. The category the sold-comps database knows best.",
        },
    ];

    public static ResaleCategory Anything => All[0];

    /// <summary>
    /// Resolves a category id, falling back to <see cref="Anything"/>. Unknown ids are never an
    /// error: a stale bookmark or a hand-typed parameter should search everything, not fail.
    /// </summary>
    public static ResaleCategory Resolve(string? id) =>
        All.FirstOrDefault(c => c.Id.Equals((id ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) ?? Anything;

    public static List<ResaleCategoryInfo> Describe() => All.Select(c => new ResaleCategoryInfo
    {
        Id = c.Id, Label = c.Label, Group = c.Group, Hint = c.Hint,
        ValuedByHand = c.ValuedByHand, BoardSearchable = true,
    }).ToList();

    /// <summary>
    /// Stamps a category (and, where it applies, a vehicle parse) onto every listing a scan found.
    /// </summary>
    /// <remarks>
    /// Called once per scan, at the pipeline edge, before anything is priced. Listings a source
    /// already classified — craigslist knows which board it searched — keep that answer; everything
    /// else is worked out from the title with <paramref name="requested"/> as a hint.
    /// </remarks>
    public static void ClassifyAll(IEnumerable<LocalSupplyListing> listings, ResaleCategory? requested = null)
    {
        foreach (var listing in listings) Classify(listing, requested);
    }

    /// <summary>Classifies one listing in place, and returns the category it landed in.</summary>
    public static ResaleCategory Classify(LocalSupplyListing listing, ResaleCategory? requested = null)
    {
        var vehicle = VehicleTitleParser.Parse(listing.Title, listing.DetailText);
        var category = Decide(listing, vehicle, requested ?? Anything);

        listing.CategoryId = category.Id;
        listing.CategoryLabel = category.Label;
        // Carried only where it means something. A year and a make on a set of floor mats is
        // trivia; on a truck it is the identity, the group key and the search query.
        listing.Vehicle = category.IsVehicle && vehicle is not null ? vehicle : null;

        return category;
    }

    private static ResaleCategory Decide(LocalSupplyListing listing, VehicleDetails? vehicle, ResaleCategory requested)
    {
        // The source already knew. Craigslist searches one board at a time, so a row off the cars
        // board is a car — a stronger statement than anything a title parser can make, and the one
        // case where a listing called "CLEAN TITLE RUNS GREAT" classifies correctly.
        if (listing.CategoryId.Length > 0)
        {
            var stamped = All.FirstOrDefault(c => c.Id.Equals(listing.CategoryId, StringComparison.OrdinalIgnoreCase));
            if (stamped is not null) return stamped;
        }

        var haystack = $"{listing.Title} {listing.Retailer}".ToLowerInvariant();

        // A part that names a vehicle is a parcel item, and this is the check that keeps it one.
        // "2024 Ford F-150 tailgate" carries a year and a make; costing it a flat vehicle fee and a
        // title transfer would turn a perfectly ordinary $180 flip into a loss.
        var parts = VehicleTitleParser.LooksLikeParts(listing.Title);

        if (!parts && LooksLikeVehicle(vehicle, haystack, requested))
        {
            var family = VehicleFamily(haystack);
            if (family is not null) return family;
            // Identified as a vehicle but not as a KIND of vehicle. The requested board decides when
            // there is one, because a seller who searched boats and got "1998 Bayliner" meant a boat.
            if (requested.IsVehicle) return requested;
            return Resolve(CarsId);
        }

        // Everything that isn't a vehicle, in catalog order — which is what puts "travel trailer"
        // ahead of "trailer" and "pressure washer" ahead of "washer".
        foreach (var candidate in All.Where(c => !c.IsDefault && !c.IsVehicle))
        {
            if (Matches(candidate, haystack)) return candidate;
        }

        // Nothing in the title said. A requested vehicle board must NOT be applied here — that is
        // exactly how a $40 hubcap in a cars search gets costed as a car — but a requested parcel or
        // bulky category is harmless and is what the seller asked for.
        return requested.IsVehicle ? Anything : requested;
    }

    /// <summary>
    /// Whether this listing is a vehicle rather than something that merely mentions one.
    /// </summary>
    /// <remarks>
    /// A year AND a make is the standing bar: either alone is far too common. "Ford" is on a floor
    /// mat and "2011" is inside a model number, but "2011 Ford" in a classifieds title is a Ford
    /// from 2011 almost every time. The bar drops to either-one only when the seller explicitly
    /// asked for a vehicle category, because then the search itself is the second piece of evidence.
    /// </remarks>
    private static bool LooksLikeVehicle(VehicleDetails? vehicle, string haystack, ResaleCategory requested)
    {
        if (vehicle is null) return requested.IsVehicle && VehicleFamily(haystack) is not null;

        if (vehicle.IsIdentified) return true;

        // An odometer reading is a vehicle fact in its own right: nothing else in local classifieds
        // is advertised with mileage on it.
        if (vehicle.Mileage is > 0 && (vehicle.Year is not null || vehicle.Make.Length > 0)) return true;

        if (!requested.IsVehicle) return VehicleFamily(haystack) is not null && vehicle.Year is not null;

        return vehicle.Year is not null || vehicle.Make.Length > 0 || VehicleFamily(haystack) is not null;
    }

    /// <summary>Which vehicle board a title belongs to, or null when it names no kind at all.</summary>
    private static ResaleCategory? VehicleFamily(string haystack) =>
        All.Where(c => c.IsVehicle).FirstOrDefault(c => Matches(c, haystack));

    // Whole words only, and the vetoes checked after the matches: "cargo trailer" is not a car and a
    // pressure washer is not an appliance, and both would be if this were a substring test.
    private static bool Matches(ResaleCategory category, string haystack) =>
        category.Keywords.Any(k => VehicleTitleParser.ContainsWord(haystack, k))
        && !category.ExcludeKeywords.Any(k => VehicleTitleParser.ContainsWord(haystack, k));

    /// <summary>
    /// What each category contributed to a scan, biggest first — the data behind the board's
    /// category filter. Only categories that actually appear are listed: an empty option in a
    /// filter is a dead end the seller has to discover by clicking it.
    /// </summary>
    public static List<ResaleCategoryTally> Tally(IEnumerable<LocalArbitrageOpportunity> rows) =>
        rows.GroupBy(r => r.CategoryId.Length > 0 ? r.CategoryId : AnythingId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ResaleCategoryTally
            {
                Id = g.Key,
                Label = Resolve(g.Key).Label,
                Count = g.Count(),
                PricedCount = g.Count(r => r.EbayExpectedSale is > 0),
            })
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
