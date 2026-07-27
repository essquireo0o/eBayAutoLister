using System.Text.RegularExpressions;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Year, make, model and mileage out of a classifieds title.
///
/// Vehicle listings are the one corner of local classifieds where the facts are reliably in the
/// title: "2011 Toyota Tundra SR5 4x4 crew cab 137k miles" carries the identity, the age and the
/// wear before the body text starts. That is worth parsing for three reasons, and only the first
/// is cosmetic:
///   • the row can show what the thing actually is rather than the seller's ad copy;
///   • two ads for the same truck group into one lookup (see <see cref="GroupKey"/>), where the
///     generic product normalizer would key them on "toyota" and merge a Tundra with a Camry;
///   • a sold-listings search the seller runs by hand has to ask for "2011 Toyota Tundra", not for
///     "MUST SEE!! CLEAN TITLE 2011 Tundra $$$".
/// </summary>
/// <remarks>
/// Pure and total: no network, no state, and every field is null or empty when the listing did not
/// say. Nothing here guesses — an unstated mileage is unknown, never zero, because a zero-mile
/// truck is a very different thing from a truck that didn't mention its miles.
/// </remarks>
public static class VehicleTitleParser
{
    // A model year, bounded at both ends. The upper bound sits a little past today so next year's
    // models parse, and the lower one keeps four-digit prices and serial numbers out.
    private static readonly Regex YearRx = new(@"\b(19[3-9]\d|20[0-4]\d)\b", RegexOptions.Compiled);

    // "137k miles", "137,000 miles", "137000 mi", "miles: 137k". The k-suffixed form is matched
    // first because "137k" also matches the plain-digits branch and would read as 137 miles.
    private static readonly Regex MileageKRx = new(
        @"\b(\d{1,3}(?:\.\d)?)\s*k\b(?:\s*(?:mi|mile|miles|mileage))?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MileageRx = new(
        @"\b(\d{1,3}(?:,\d{3})+|\d{4,7})\s*(?:mi|mile|miles|mileage|odometer)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Tractors, excavators, generators and boats state wear in hours, not miles.
    private static readonly Regex HoursRx = new(
        @"\b(\d{1,3}(?:,\d{3})+|\d{1,6})\s*(?:hr|hrs|hour|hours)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The makes worth recognising, longest-phrase-first so "Land Rover" and "Mercedes Benz" win
    /// over "Rover" and "Mercedes". Deliberately not exhaustive: a make that isn't here costs a
    /// tidier label and a better group key, never a dropped row — see
    /// <see cref="ResaleCategoryCatalog"/>, which never decides "this is a vehicle" on a make alone.
    /// </summary>
    public static readonly string[] Makes =
    [
        // Cars, trucks and SUVs
        "mercedes-benz", "mercedes benz", "land rover", "range rover", "alfa romeo", "aston martin",
        "chevrolet", "chevy", "cadillac", "chrysler", "lincoln", "mitsubishi", "volkswagen", "vw",
        "toyota", "honda", "nissan", "hyundai", "subaru", "porsche", "ferrari", "maserati", "bentley",
        "ford", "dodge", "ram", "gmc", "buick", "jeep", "mazda", "lexus", "acura", "infiniti",
        "audi", "bmw", "kia", "volvo", "tesla", "mini", "saturn", "pontiac", "oldsmobile", "mercury",
        "isuzu", "suzuki", "scion", "rivian", "lucid", "genesis", "freightliner", "peterbilt", "kenworth",
        // Powersports
        "harley-davidson", "harley davidson", "harley", "can-am", "can am", "ski-doo", "sea-doo",
        "arctic cat", "kawasaki", "yamaha", "ducati", "triumph", "aprilia", "husqvarna", "ktm",
        "polaris", "indian", "victory", "vespa", "cfmoto",
        // Boats
        "boston whaler", "sea ray", "grady-white", "bayliner", "chaparral", "mastercraft", "malibu",
        "crestliner", "alumacraft", "starcraft", "ranger", "lund", "tracker", "nitro", "skeeter",
        "carolina skiff", "key west", "robalo", "cobia", "yellowfin", "regal", "monterey", "four winns",
        // RVs and campers
        "winnebago", "airstream", "jayco", "forest river", "keystone", "coachmen", "thor", "fleetwood",
        "grand design", "heartland", "tiffin", "newmar", "lance", "casita", "shasta",
        // Tractors and heavy equipment
        "john deere", "kubota", "massey ferguson", "new holland", "case ih", "caterpillar", "bobcat",
        "komatsu", "takeuchi", "mahindra", "kioti", "yanmar", "allis chalmers", "international harvester",
    ];

    /// <summary>
    /// Titles that name a vehicle brand but sell a box-shippable PART. This is the single most
    /// expensive misread in the feature: "2024 Ford F-150 tailgate" and "Toyota trailer hitch" both
    /// carry a year and a make, and treating either as a vehicle would cost it a $125 flat fee, a
    /// title transfer and a refused valuation — on a $60 parcel item that the ordinary parcel path
    /// prices perfectly well.
    /// </summary>
    private static readonly Regex PartsRx = new(
        @"\b(part|parts|hitch|tailgate|bumper|fender|hood|grille|grill|mirror|headlight|taillight|" +
        @"tail\s?light|head\s?light|wheel|wheels|rim|rims|tire|tires|seat\s?cover|floor\s?mat|" +
        @"mats?|alternator|starter|radiator|ecu|ecm|pcm|injector|turbo|manifold|axle|driveshaft|" +
        @"differential|transmission|engine|motor\s?only|carburetor|carb|clutch|brake\s?pad|rotor|" +
        @"muffler|exhaust|catalytic|spoiler|door\s?panel|window\s?regulator|key\s?fob|owners?\s?manual|" +
        @"service\s?manual|shop\s?manual|brochure|emblem|badge|decal|hubcap|lug\s?nut|cover|tonneau|" +
        @"tool\s?box|toolbox|rack|roof\s?rack|bike\s?rack|ball\s?mount|receiver|jack|winch|" +
        @"battery|alternator|radio|stereo|head\s?unit|gauge\s?cluster|steering\s?wheel)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// True when the title is selling a part rather than the vehicle. Checked before anything else
    /// decides a listing is a vehicle.
    /// </summary>
    public static bool LooksLikeParts(string? text) => PartsRx.IsMatch(text ?? "");

    /// <summary>
    /// Reads whatever the listing stated. Returns null when there is nothing vehicle-shaped in it at
    /// all — no year, no known make, no odometer — so the common case costs one regex and answers
    /// "not a vehicle" rather than an object full of blanks.
    /// </summary>
    /// <param name="title">The listing title, already cleaned of the price and neighbourhood.</param>
    /// <param name="detail">
    /// The post body where the source publishes one. Only ever read for the WEAR figure: mileage is
    /// routinely in the body when it didn't fit the title, while a year or a make found three
    /// paragraphs down is as likely to be about the seller's other car.
    /// </param>
    public static VehicleDetails? Parse(string? title, string? detail = null)
    {
        var text = (title ?? "").Trim();
        if (text.Length == 0) return null;

        var lower = text.ToLowerInvariant();

        var year = YearRx.Match(text) is { Success: true } y && int.TryParse(y.Groups[1].Value, out var parsed)
            ? parsed : (int?)null;

        var make = Makes.FirstOrDefault(m => ContainsWord(lower, m)) ?? "";
        var mileage = Mileage(text) ?? Mileage(detail);
        var hours = Hours(text) ?? Hours(detail);

        if (year is null && make.Length == 0 && mileage is null && hours is null) return null;

        return new VehicleDetails
        {
            Year = year,
            Make = TitleCase(make),
            Model = ModelAfter(text, lower, make),
            Mileage = mileage,
            EngineHours = hours,
        };
    }

    /// <summary>
    /// The one or two words after the make — "Tundra SR5", "F-150", "Road King". Cut at two because
    /// a third word is reliably trim, condition or shouting, and because the group key below is
    /// meant to merge two ads for the same truck rather than split them on adjectives.
    /// </summary>
    private static string ModelAfter(string text, string lower, string make)
    {
        if (make.Length == 0) return "";

        var at = lower.IndexOf(make, StringComparison.Ordinal);
        if (at < 0) return "";

        var tail = text[(at + make.Length)..];
        var words = tail.Split([' ', ',', '-', '/', '|'], StringSplitOptions.RemoveEmptyEntries)
            // A model token has a letter in it: "4x4", "2011" and "$4,500" are not models, and the
            // year in "Toyota 2011 Tundra" must not become the model.
            .Where(w => w.Any(char.IsAsciiLetter))
            .Select(w => new string(w.Where(c => char.IsAsciiLetterOrDigit(c)).ToArray()))
            .Where(w => w.Length is > 0 and <= 12)
            .Take(2)
            .ToList();

        // A bare descriptor is not a model. "Ford truck for sale" identifies nothing a comp search
        // could use, and printing "Ford Truck" as the identity would imply it did.
        string[] filler = ["for", "sale", "truck", "car", "suv", "van", "boat", "rv", "trailer", "clean", "runs", "great", "nice"];
        words = words.Where(w => !filler.Contains(w.ToLowerInvariant())).ToList();

        return words.Count == 0 ? "" : string.Join(' ', words.Select(TitleCase));
    }

    private static int? Mileage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var k = MileageKRx.Match(text);
        if (k.Success && decimal.TryParse(k.Groups[1].Value, out var thousands))
        {
            var miles = (int)Math.Round(thousands * 1000m);
            if (miles is > 0 and <= 999_000) return miles;
        }

        var plain = MileageRx.Match(text);
        if (plain.Success && int.TryParse(plain.Groups[1].Value.Replace(",", ""), out var exact)
            && exact is > 0 and <= 1_500_000)
        {
            return exact;
        }

        return null;
    }

    private static int? Hours(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var m = HoursRx.Match(text);
        return m.Success && int.TryParse(m.Groups[1].Value.Replace(",", ""), out var hours) && hours is > 0 and <= 90_000
            ? hours : null;
    }

    /// <summary>
    /// The key two listings of the same vehicle share — "2011|toyota|tundra".
    ///
    /// Used instead of the generic product signature for vehicle rows, because that signature keys
    /// on brand and model number and a classifieds title has neither: it would put a 2011 Tundra
    /// and a 2003 Camry in one group under "toyota" and price them off one lookup. Returns empty
    /// when the listing didn't identify the vehicle, which leaves the row in its own group — the
    /// safe direction, since a group is a shared price.
    /// </summary>
    public static string GroupKey(VehicleDetails? vehicle)
    {
        if (vehicle is null || !vehicle.IsIdentified) return "";
        return $"{vehicle.Year}|{vehicle.Make}|{vehicle.Model}".ToLowerInvariant();
    }

    /// <summary>
    /// What to search sold listings for: the identity and nothing else. A seller's own title carries
    /// the price, the neighbourhood and three exclamation marks, and every one of them narrows a
    /// comp search to zero results.
    /// </summary>
    public static string SearchQuery(VehicleDetails? vehicle, string fallbackTitle)
    {
        var label = vehicle?.Label ?? "";
        return label.Length > 0 ? label : (fallbackTitle ?? "").Trim();
    }

    /// <summary>
    /// Whole-word (or whole-phrase) containment, case-sensitive on an already-lowered haystack.
    /// </summary>
    /// <remarks>
    /// Plain <c>Contains</c> is wrong here in ways that cost real money and are invisible until they
    /// do: "cargo trailer" contains "car", "dishwasher" contains "washer", and either one would send
    /// a listing to the wrong category — which now means the wrong fee model and the wrong valuation
    /// rule, not merely the wrong label.
    /// </remarks>
    public static bool ContainsWord(string haystack, string needle)
    {
        var at = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            var beforeOk = at == 0 || !char.IsAsciiLetterOrDigit(haystack[at - 1]);
            var end = at + needle.Length;
            var afterOk = end >= haystack.Length || !char.IsAsciiLetterOrDigit(haystack[end]);
            if (beforeOk && afterOk) return true;
            at = haystack.IndexOf(needle, at + 1, StringComparison.Ordinal);
        }
        return false;
    }

    // Makes and model codes that are initialisms. Title-casing them ("Bmw", "Gmc") makes the
    // identity harder to read rather than easier, which is the only thing this casing is for.
    private static readonly string[] Initialisms = ["bmw", "vw", "gmc", "ktm", "cfmoto", "rv", "utv", "atv", "sr5", "xlt", "lt", "ls"];

    private static string TitleCase(string word)
    {
        if (word.Length == 0) return "";
        // A model code carries digits and is already written the way it is stamped on the tailgate:
        // "F150", "S19", "4Runner". Recasing those loses information.
        if (word.Any(char.IsAsciiDigit) || Initialisms.Contains(word.ToLowerInvariant()))
            return word.ToUpperInvariant();

        // Split on spaces AND hyphens so "harley-davidson" and "mercedes-benz" come back with both
        // halves capitalised, the way the badge reads.
        var spaced = string.Join(' ', word.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => string.Join('-', w.Split('-', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()))));

        return spaced;
    }
}
