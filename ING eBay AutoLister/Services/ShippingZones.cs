using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Turns "where do I ship from" into "what does an average sale actually cost me".
/// </summary>
/// <remarks>
/// <para>
/// Every carrier rate that is not flat-rate is priced by zone, and a zone only exists relative to
/// an origin. Quoting a single zone is the mistake most shipping calculators make: a seller in
/// Ohio quoting zone 4 is understating half their sales, and a seller in California quoting zone 8
/// is scaring themselves off items that are fine. Neither number is what a real month costs.
/// </para>
/// <para>
/// So instead of one zone, this produces a probability distribution: the share of US buyers who
/// will land in each zone from a given origin, weighted by population. Every cost the engine
/// reports downstream is the expectation over that distribution, with the near and far tails kept
/// alongside it — because the tails are where the seller loses money on free shipping.
/// </para>
/// <para>
/// Resolution is the ZIP prefix (leading digit), not the ZIP. That is deliberate: a ten-region
/// model needs no data file, no lookup service and no network, and a zone boundary is 300+ miles
/// wide, so the extra precision of a full ZIP-to-zone matrix would move the expected cost by cents.
/// The honest thing is to be approximate and say so, rather than exact-looking and stale.
/// </para>
/// </remarks>
public static class ShippingZones
{
    /// <summary>A ZIP-prefix region: where its people are, and how many of them there are.</summary>
    private sealed record Region(int Prefix, string Name, double Lat, double Lon, double PopulationShare);

    // Population-weighted centroids for each ZIP leading digit, with each region's share of the US
    // population. Shares are normalised at use, so they only have to be right relative to each
    // other — which keeps this maintainable without pretending to census precision.
    private static readonly Region[] Regions =
    [
        new(0, "New England, NJ",             41.8, -71.4,  6.5),
        new(1, "NY, eastern PA",              41.5, -75.5,  8.5),
        new(2, "DC, VA, NC, SC, WV",          37.5, -78.5, 10.5),
        new(3, "FL, GA, AL, TN, MS",          32.5, -84.0, 12.5),
        new(4, "OH, MI, IN, KY",              40.5, -84.5, 10.0),
        new(5, "MN, WI, IA, the Dakotas, MT", 44.0, -93.5,  5.5),
        new(6, "IL, MO, KS, NE",              39.5, -92.0,  7.0),
        new(7, "TX, LA, OK, AR",              31.5, -96.5, 11.5),
        new(8, "CO, AZ, UT, NV, NM, ID",      38.0, -108.5, 6.5),
        new(9, "CA, WA, OR, AK, HI",          38.0, -121.0, 17.5),
    ];

    /// <summary>
    /// Average distance to a buyer inside the seller's own ZIP region, in miles.
    /// </summary>
    /// <remarks>
    /// Without this the origin region would sit at distance zero and price as zone 1, which would
    /// understate roughly a tenth of every seller's sales. A ZIP region is several hundred miles
    /// across, so a typical in-region buyer is a zone 2-3 buyer, not a zone 1 buyer.
    /// </remarks>
    private const double IntraRegionMiles = 210;

    /// <summary>The zone every rate table is anchored on when there is no origin to work from.</summary>
    public const int DefaultZone = 5;

    /// <summary>USPS zone boundaries, in miles. Index i holds the upper bound of zone i+1.</summary>
    private static readonly double[] ZoneUpperMiles = [50, 150, 300, 600, 1000, 1400, 1800];

    public const int MinZone = 1;
    public const int MaxZone = 8;

    /// <summary>The zone a package travelling <paramref name="miles"/> falls into.</summary>
    public static int ZoneForDistance(double miles)
    {
        for (var i = 0; i < ZoneUpperMiles.Length; i++)
            if (miles <= ZoneUpperMiles[i]) return i + 1;
        return MaxZone;
    }

    /// <summary>
    /// The share of US buyers in each zone, shipping from <paramref name="originZip"/>.
    /// </summary>
    /// <remarks>
    /// An unparseable or missing ZIP yields the national-average mix — computed by running every
    /// origin region against every destination region and weighting by both — rather than a
    /// hardcoded guess or an exception. A seller who has not filled in their address still gets a
    /// number, and it is the right number for "a typical US seller".
    /// </remarks>
    public static List<ZoneShare> MixFor(string? originZip)
    {
        var origin = RegionFor(originZip);
        var weights = new Dictionary<int, double>();

        if (origin is null)
        {
            foreach (var from in Regions)
                Accumulate(weights, from, from.PopulationShare / 100.0);
        }
        else
        {
            Accumulate(weights, origin, 1.0);
        }

        var total = weights.Values.Sum();
        if (total <= 0) return [new ZoneShare { Zone = DefaultZone, SharePercent = 100m, Regions = "" }];

        return weights
            .Where(kv => kv.Value > 0)
            .OrderBy(kv => kv.Key)
            .Select(kv => new ZoneShare
            {
                Zone = kv.Key,
                SharePercent = (decimal)Math.Round(kv.Value / total * 100.0, 1),
                Regions = RegionsInZone(origin, kv.Key),
            })
            .ToList();
    }

    // Spreads one origin's population-weighted destinations across zones, scaled by originWeight.
    private static void Accumulate(Dictionary<int, double> weights, Region origin, double originWeight)
    {
        foreach (var to in Regions)
        {
            var miles = ReferenceEquals(origin, to) || origin.Prefix == to.Prefix
                ? IntraRegionMiles
                : DistanceMiles(origin.Lat, origin.Lon, to.Lat, to.Lon);

            var zone = ZoneForDistance(miles);
            weights[zone] = weights.GetValueOrDefault(zone) + to.PopulationShare * originWeight;
        }
    }

    private static string RegionsInZone(Region? origin, int zone)
    {
        if (origin is null) return "";
        var names = Regions
            .Where(to =>
            {
                var miles = origin.Prefix == to.Prefix ? IntraRegionMiles : DistanceMiles(origin.Lat, origin.Lon, to.Lat, to.Lon);
                return ZoneForDistance(miles) == zone;
            })
            .Select(r => r.Name);
        return string.Join("; ", names);
    }

    /// <summary>
    /// Expected value of <paramref name="costForZone"/> over the buyer distribution.
    /// </summary>
    public static decimal ExpectedOver(IEnumerable<ZoneShare> mix, Func<int, decimal> costForZone)
    {
        decimal weighted = 0m, shares = 0m;
        foreach (var slice in mix)
        {
            weighted += costForZone(slice.Zone) * slice.SharePercent;
            shares += slice.SharePercent;
        }
        return shares > 0m ? Math.Round(weighted / shares, 2) : 0m;
    }

    /// <summary>
    /// Share of buyers, by population, whose label costs more than <paramref name="collected"/>.
    /// </summary>
    /// <remarks>
    /// This is the number that makes free shipping honest. "Free shipping at your average cost"
    /// sounds safe and is a coin flip; saying that it loses money on 38% of sales is the same fact
    /// in a form the seller can act on.
    /// </remarks>
    public static decimal ShareAboveCost(IEnumerable<ZoneShare> mix, Func<int, decimal> costForZone, decimal collected)
    {
        decimal over = 0m, shares = 0m;
        foreach (var slice in mix)
        {
            if (costForZone(slice.Zone) > collected) over += slice.SharePercent;
            shares += slice.SharePercent;
        }
        return shares > 0m ? Math.Round(over / shares * 100m, 1) : 0m;
    }

    /// <summary>The closest zone any real buyer sits in — not zone 1 unless somebody is there.</summary>
    public static int NearestZone(IEnumerable<ZoneShare> mix) =>
        mix.Any() ? mix.Min(z => z.Zone) : DefaultZone;

    /// <summary>The farthest zone with real buyers in it: where free shipping goes wrong.</summary>
    public static int FarthestZone(IEnumerable<ZoneShare> mix) =>
        mix.Any() ? mix.Max(z => z.Zone) : DefaultZone;

    private static Region? RegionFor(string? zip)
    {
        if (string.IsNullOrWhiteSpace(zip)) return null;
        var digits = new string(zip.Where(char.IsDigit).ToArray());
        if (digits.Length < 5) return null;   // a partial ZIP is a typo, not a location
        return Regions.FirstOrDefault(r => r.Prefix == digits[0] - '0');
    }

    // Great-circle distance. Carriers price on rate-zone charts rather than straight-line miles,
    // but the charts are themselves built from distance bands, so this lands in the right band
    // essentially always and in the neighbouring one at a boundary.
    private static double DistanceMiles(double lat1, double lon1, double lat2, double lon2)
    {
        const double EarthRadiusMiles = 3958.8;
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return EarthRadiusMiles * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
