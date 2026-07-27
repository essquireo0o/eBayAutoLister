namespace ING_eBay_AutoLister.Services;

/// <summary>
/// How long cover normally runs, and whether it survives a resale — the two facts a listing almost
/// never states and the detector needs anyway.
/// </summary>
/// <param name="Label">How the row names it.</param>
/// <param name="Months">The published term. Capped on use by <see cref="WarrantySelectors.MaxCreditedMonths"/>.</param>
/// <param name="Transfers">
/// Whether the person the reseller sells to can realistically claim on it. See
/// <see cref="WarrantyCatalog"/> for what this is and is not asserting.
/// </param>
public sealed record WarrantyTerm(string Label, int Months, bool Transfers);

/// <summary>
/// The published terms behind the warranty finder: refurbishment programmes, and the standard
/// factory cover for the brands a reseller actually meets.
/// </summary>
/// <remarks>
/// <para>
/// <b>What <see cref="WarrantyTerm.Transfers"/> means.</b> Not a legal opinion. It is the answer to a
/// commercial question: can the reseller's own eBay listing honestly say "still under warranty until
/// X", and will the buyer get something if they try to claim? For serial-tracked consumer electronics
/// the answer is routinely yes — the cover is attached to the unit and the service desk looks up the
/// serial. For most cordless power tools it is explicitly no: the terms name the original purchaser
/// and require the receipt, and pretending otherwise would put a premium on a resale line the seller
/// cannot truthfully write. Those brands are listed with <c>false</c> for exactly that reason.
/// </para>
/// <para>
/// <b>What this catalog is not.</b> It is not a source of warranties. Nothing here creates cover on a
/// listing that never mentioned any — a brand's standard term is only ever used to date cover the
/// listing itself claimed, or to produce an <see cref="Models.WarrantyEvidence.Estimated"/> reading,
/// which <see cref="WarrantyPricer"/> is not permitted to charge a cent for. Terms move; they are
/// here to be edited in one place when they do.
/// </para>
/// </remarks>
public static class WarrantyCatalog
{
    /// <summary>
    /// Refurbishment and open-box programmes with published cover, longest-standing first. Matched
    /// against the listing text and the retailer name, most specific phrase first — "apple certified
    /// refurbished" has to win over the bare "certified refurbished" below it.
    /// </summary>
    public static readonly IReadOnlyList<(string Phrase, WarrantyTerm Term)> Programs =
    [
        // Apple's refurbished stock ships with the same one-year cover as new, and Apple's warranty
        // is looked up by serial — which is why this is the strongest programme on the list.
        ("apple certified refurbished", new WarrantyTerm("Apple Certified Refurbished", 12, true)),
        ("apple refurbished",           new WarrantyTerm("Apple Certified Refurbished", 12, true)),
        // Dell and HP refurbished outlet stock carries the balance of a service-tag warranty, and a
        // service tag does not care who owns the machine.
        ("dell outlet",                 new WarrantyTerm("Dell Outlet", 12, true)),
        ("dell refurbished",            new WarrantyTerm("Dell Refurbished", 12, true)),
        ("hp certified refurbished",    new WarrantyTerm("HP Certified Refurbished", 12, true)),
        ("lenovo certified refurbished",new WarrantyTerm("Lenovo Certified Refurbished", 12, true)),
        // Amazon Renewed is a guarantee Amazon gives its own purchaser. It is real protection for the
        // reseller and it does not follow the item onward, so it never earns a resale premium.
        ("amazon renewed",              new WarrantyTerm("Amazon Renewed", 3, false)),
        ("renewed premium",             new WarrantyTerm("Amazon Renewed Premium", 12, false)),
        // eBay Refurbished cover is backed by the seller/brand that listed it — same story.
        ("ebay refurbished",            new WarrantyTerm("eBay Refurbished", 12, false)),
        // A Best Buy open-box unit is not refurbished at all: it is a returned new item, and what it
        // carries is the manufacturer's own warranty, dated from when it was first sold.
        ("geek squad certified",        new WarrantyTerm("Geek Squad Certified Refurbished", 3, false)),
        ("best buy open-box",           new WarrantyTerm("Best Buy Open-Box", 12, true)),
        ("best buy open box",           new WarrantyTerm("Best Buy Open-Box", 12, true)),
        // Bare programme wording, in the order the phrases actually appear. Manufacturer-refurbished
        // stock is re-certified by the maker against the serial; seller-refurbished is one person's
        // word and gets no term at all until they state one.
        ("manufacturer refurbished",    new WarrantyTerm("Manufacturer refurbished", 3, true)),
        ("factory refurbished",         new WarrantyTerm("Factory refurbished", 3, true)),
        ("certified refurbished",       new WarrantyTerm("Certified refurbished", 3, false)),
        ("recertified",                 new WarrantyTerm("Recertified", 3, false)),
    ];

    /// <summary>
    /// Standard factory cover by brand. Read for two things only: dating a warranty the listing
    /// already claimed, and estimating one on an unopened or recently-bought item.
    /// </summary>
    /// <remarks>
    /// The <c>false</c> entries are the brands whose published terms name the original purchaser —
    /// almost all of the cordless tool market. Those tools are excellent flips and their warranty is
    /// worth something to the person buying them and nothing to the person they sell to, which is a
    /// distinction the board would otherwise get backwards on the highest-volume category on it.
    /// </remarks>
    public static readonly IReadOnlyList<(string Brand, WarrantyTerm Term)> Brands =
    [
        // ── Serial-tracked electronics: the warranty follows the unit ────────────────────────────
        ("apple",       new WarrantyTerm("Apple", 12, true)),
        ("macbook",     new WarrantyTerm("Apple", 12, true)),
        ("iphone",      new WarrantyTerm("Apple", 12, true)),
        ("ipad",        new WarrantyTerm("Apple", 12, true)),
        ("dell",        new WarrantyTerm("Dell", 12, true)),
        ("alienware",   new WarrantyTerm("Alienware", 12, true)),
        ("lenovo",      new WarrantyTerm("Lenovo", 12, true)),
        ("thinkpad",    new WarrantyTerm("Lenovo", 12, true)),
        ("hp ",         new WarrantyTerm("HP", 12, true)),
        ("asus",        new WarrantyTerm("ASUS", 12, true)),
        ("acer",        new WarrantyTerm("Acer", 12, true)),
        ("msi",         new WarrantyTerm("MSI", 12, true)),
        ("razer",       new WarrantyTerm("Razer", 12, true)),
        ("dyson",       new WarrantyTerm("Dyson", 24, true)),
        ("bose",        new WarrantyTerm("Bose", 12, true)),
        ("sonos",       new WarrantyTerm("Sonos", 12, true)),
        // ASIC miners are warrantied against the serial and resold constantly while still covered —
        // the single category where remaining cover most changes what a unit fetches.
        ("antminer",    new WarrantyTerm("Bitmain", 12, true)),
        ("bitmain",     new WarrantyTerm("Bitmain", 12, true)),
        ("whatsminer",  new WarrantyTerm("MicroBT", 12, true)),
        ("avalon",      new WarrantyTerm("Canaan", 12, true)),

        // ── Original-purchaser terms: real cover, worth nothing on resale ────────────────────────
        ("dewalt",      new WarrantyTerm("DeWalt", 36, false)),
        ("milwaukee",   new WarrantyTerm("Milwaukee", 60, false)),
        ("makita",      new WarrantyTerm("Makita", 36, false)),
        ("ryobi",       new WarrantyTerm("Ryobi", 36, false)),
        ("ridgid",      new WarrantyTerm("RIDGID", 36, false)),
        ("bosch",       new WarrantyTerm("Bosch", 12, false)),
        ("ego power",   new WarrantyTerm("EGO", 60, false)),
        ("greenworks",  new WarrantyTerm("Greenworks", 48, false)),
        ("traeger",     new WarrantyTerm("Traeger", 36, false)),
        ("weber",       new WarrantyTerm("Weber", 36, false)),
        ("samsung",     new WarrantyTerm("Samsung", 12, false)),
        ("lg ",         new WarrantyTerm("LG", 12, false)),
        ("sony",        new WarrantyTerm("Sony", 12, false)),
        ("nintendo",    new WarrantyTerm("Nintendo", 12, false)),
        ("playstation", new WarrantyTerm("PlayStation", 12, false)),
        ("xbox",        new WarrantyTerm("Xbox", 12, false)),
        ("garmin",      new WarrantyTerm("Garmin", 12, false)),
        ("gopro",       new WarrantyTerm("GoPro", 12, false)),
        ("kitchenaid",  new WarrantyTerm("KitchenAid", 12, false)),
        ("irobot",      new WarrantyTerm("iRobot", 12, false)),
        ("roomba",      new WarrantyTerm("iRobot", 12, false)),
        ("peloton",     new WarrantyTerm("Peloton", 12, false)),
        ("vitamix",     new WarrantyTerm("Vitamix", 84, false)),
    ];

    /// <summary>
    /// The refurbishment or open-box programme this listing belongs to, or null. Checked against the
    /// listing text and the retailer together, because "Open-Box" on a row whose retailer is Best Buy
    /// is a specific programme with specific terms and the same word elsewhere is not.
    /// </summary>
    public static WarrantyTerm? ProgramFor(string? text, string? retailer)
    {
        var haystack = $"{retailer} {text}";
        if (string.IsNullOrWhiteSpace(haystack)) return null;

        foreach (var (phrase, term) in Programs)
            if (haystack.Contains(phrase, StringComparison.OrdinalIgnoreCase)) return term;

        return null;
    }

    /// <summary>
    /// What this brand's factory cover normally runs to, or null when the brand isn't one this app
    /// claims to know. Null is the honest answer far more often than it is a gap: a term invented for
    /// an unrecognised brand is a date on the row that nothing stands behind.
    /// </summary>
    public static WarrantyTerm? BrandFor(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        // Padded so the two-letter brands ("hp ", "lg ") can be matched as words without a regex —
        // "lg" is inside "bulging" and "hp" inside "sharp".
        var padded = $" {title} ";
        foreach (var (brand, term) in Brands)
            if (padded.Contains(brand.EndsWith(' ') ? $" {brand}" : brand, StringComparison.OrdinalIgnoreCase))
                return term;

        return null;
    }

    /// <summary>
    /// Whether a warranty of this kind is assumed to survive the resale when nothing said either way.
    /// </summary>
    /// <remarks>
    /// A seller's personal promise never does — it was made to the reseller. Everything else defaults
    /// to yes, and the reason is what the default is FOR: it decides whether the reseller may write
    /// "still under manufacturer warranty" in their own listing. For a stated factory or programme
    /// warranty on a serial-tracked good that line is ordinarily true, and the brands where it is not
    /// are named above. The money that rides on the default is bounded hard either way — see
    /// <see cref="WarrantySelectors.MaxUpliftPercent"/> and <see cref="WarrantySelectors.MaxUpliftDollars"/>.
    /// </remarks>
    public static bool TransfersByDefault(string kind) => kind != Models.WarrantyKinds.Seller;
}
