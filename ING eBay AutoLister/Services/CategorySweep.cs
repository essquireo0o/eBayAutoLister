namespace ING_eBay_AutoLister.Services;

/// <summary>
/// One niche the cross-category sweep can dig in, and the keywords it digs with.
///
/// Probes are deliberately brand/model-shaped ("milwaukee m18", not "power tools"): the comps
/// lookup refuses to match on generic words alone (see MarketplaceMatcher.ImportantWords), and a
/// probe that returns 400 unrelated rows costs the same as one that returns 400 useful ones.
/// </summary>
public sealed record SweepNiche(string Id, string Label, string[] Probes);

/// <summary>
/// The category universe behind Roll the Dice, plus the rotation that makes "Roll again" mean
/// something: each roll takes the NEXT window of niches, so consecutive rolls dig entirely
/// different categories and a handful of rolls covers the whole board.
///
/// Everything here is pure and deterministic in the seed — the same seed always produces the same
/// roll (so a result can be reproduced from the number printed on it), and the seed advancing is
/// what explores new ground.
/// </summary>
public static class CategorySweep
{
    // Curated rather than derived: the sold-comps table has no category column (they're NULL for
    // every row — see MarketplaceRepository's note), so "sweep every category" has to mean
    // sweeping a list of keyword probes. Chosen for things that resell on eBay AND turn up in
    // local classifieds, which is where the two halves of a flip meet.
    private static readonly SweepNiche[] TheUniverse =
    [
        new("mining", "Crypto mining hardware", ["antminer s19", "whatsminer m30", "goldshell kd6"]),
        new("gpus", "Graphics cards", ["rtx 3080", "rtx 4070", "quadro rtx"]),
        new("consoles", "Game consoles", ["playstation 5 console", "xbox series x", "nintendo switch oled"]),
        new("retro-gaming", "Retro gaming", ["nintendo 64 console", "game boy advance", "sega genesis console"]),
        new("power-tools", "Power tools", ["milwaukee m18", "dewalt 20v max", "makita 18v lxt"]),
        new("outdoor-power", "Outdoor power equipment", ["stihl chainsaw", "ego 56v mower", "honda gx200"]),
        new("cameras", "Cameras and lenses", ["canon eos r", "nikon d750", "sony a7 iii"]),
        new("drones", "Drones", ["dji mavic", "dji mini 3", "autel evo"]),
        new("audio", "Home and pro audio", ["sonos play 5", "klipsch rp 600m", "shure sm7b"]),
        new("vacuums", "Vacuums and floor care", ["dyson v11", "roomba i7", "shark navigator"]),
        new("kitchen", "Kitchen appliances", ["kitchenaid artisan mixer", "vitamix 5200", "breville barista"]),
        new("fitness", "Fitness equipment", ["peloton bike", "concept2 rower", "bowflex 552"]),
        new("networking", "Networking and servers", ["cisco catalyst 9300", "ubiquiti dream machine", "synology ds920"]),
        new("laptops", "Laptops", ["macbook pro m1", "thinkpad x1 carbon", "dell xps 15"]),
        new("phones", "Phones and tablets", ["iphone 13 pro", "ipad pro 11", "galaxy s22 ultra"]),
        new("smart-home", "Smart home", ["nest learning thermostat", "ring doorbell pro", "lutron caseta"]),
        new("automation", "Industrial automation", ["allen bradley 1756", "fanuc a06b", "siemens simatic"]),
        new("test-gear", "Test and measurement", ["fluke 87v", "tektronix tds", "snap on scanner"]),
        new("medical", "Medical and mobility", ["resmed airsense 10", "welch allyn otoscope", "invacare wheelchair"]),
        new("instruments", "Musical instruments", ["fender stratocaster", "gibson les paul", "roland td drum"]),
        new("watches", "Watches", ["seiko skx", "citizen eco drive", "casio g shock"]),
        new("apparel", "Outerwear and workwear", ["patagonia jacket", "carhartt jacket", "arcteryx jacket"]),
        new("golf", "Golf clubs", ["titleist tsi driver", "taylormade stealth", "callaway apex irons"]),
        new("baby", "Baby gear", ["uppababy vista", "nuna pipa", "doona car seat"]),
        new("auto-parts", "Auto parts", ["ford f150 tailgate", "bmw e46 ecu", "jeep wrangler hardtop"]),
        new("lego", "LEGO and collectible sets", ["lego star wars set", "lego technic set", "lego creator expert"]),
        new("crafting", "Sewing and crafting", ["cricut maker 3", "brother sewing machine", "juki serger"]),
        new("power-storage", "Solar and power storage", ["ecoflow delta", "goal zero yeti", "victron multiplus"]),
    ];

    public static IReadOnlyList<SweepNiche> Universe => TheUniverse;

    /// <summary>
    /// The niches this roll digs: a window of <paramref name="count"/> niches that advances by
    /// <paramref name="count"/> every roll, wrapping at the end of the universe. Consecutive seeds
    /// therefore never repeat a niche, which is the whole point of "Roll again".
    /// </summary>
    public static List<SweepNiche> Select(int seed, int count)
    {
        count = Math.Clamp(count, 1, TheUniverse.Length);
        var start = Mod(Mod(seed, RollsToCoverEverything(count)) * count, TheUniverse.Length);
        return Enumerable.Range(0, count)
            .Select(i => TheUniverse[(start + i) % TheUniverse.Length])
            .ToList();
    }

    /// <summary>
    /// Which of a niche's keywords this roll uses. Rotates with the seed too, so a niche that comes
    /// round again on a later roll digs with a different keyword instead of repeating the answer.
    /// </summary>
    public static List<string> ProbesFor(SweepNiche niche, int seed, int probesPerNiche)
    {
        var wanted = Math.Clamp(probesPerNiche, 1, niche.Probes.Length);
        var start = Mod(seed, niche.Probes.Length);
        return Enumerable.Range(0, wanted)
            .Select(i => niche.Probes[(start + i) % niche.Probes.Length])
            .ToList();
    }

    /// <summary>How many rolls it takes to have dug every niche once, at this window size.</summary>
    public static int RollsToCoverEverything(int count) =>
        (int)Math.Ceiling(TheUniverse.Length / (double)Math.Clamp(count, 1, TheUniverse.Length));

    /// <summary>
    /// The seed the next roll should use. Wraps at int.MaxValue rather than overflowing — a seed is
    /// a position on a wheel, not a count of anything.
    /// </summary>
    public static int NextSeed(int seed) => seed == int.MaxValue ? 0 : seed + 1;

    // A first roll shouldn't start every seller in the same corner of the universe.
    public static int RandomSeed() => Random.Shared.Next(0, int.MaxValue);

    // True modulo — C#'s % keeps the sign of the dividend, and a negative seed from a client would
    // otherwise index backwards off the array.
    private static int Mod(int value, int modulus) => modulus <= 0 ? 0 : ((value % modulus) + modulus) % modulus;
}
