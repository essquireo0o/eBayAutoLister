using System.Text.RegularExpressions;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The brain behind the cross-category sweep: turns a haul of sold comps from many categories into a ranked
/// board of flips worth making, and says what to pay for each one.
///
/// Three jobs, all of them separable and (except the fee arithmetic, which delegates to
/// <see cref="ProfitCalculator"/>) pure:
///   1. clustering a probe's sold rows into PRODUCTS, since the comps table stores listings;
///   2. screening those products before a cent of pricing budget is spent — accessories, lots,
///      broken-item comps, sub-fee prices, dead demand and clusters too vague to be one product
///      are dropped here with a reason, not silently;
///   3. the money: break-even buy price, the price that clears the jackpot bar, and the verdict —
///      which is <see cref="LocalArbitrageAnalyzer"/>'s verdict, on its thresholds, because a
///      "jackpot" scored on friendlier arithmetic than the rest of the app would be a lie.
/// </summary>
public sealed partial class JackpotHunter(ProfitCalculator profitCalc)
{
    // Screening floors. Deliberately stricter than "any profit at all": this feature hands someone
    // a product they have never heard of and tells them to spend money on it, so thin evidence has
    // to be dropped rather than shown with a caveat nobody reads.
    public const int MinCompsToPrice = 4;
    // A cluster with no model-shaped token ("kitchenaid mixer") could be several products wearing
    // one name, so it has to bring more sold history before it's worth a lookup.
    public const int MinCompsForLooseIdentity = 6;
    public const decimal MinMedianSold = 40m;
    public const int RecentDays = 120;
    // Above this ratio between the cheapest and dearest comp, the cluster isn't one product.
    public const decimal MaxPriceSpreadRatio = 5m;

    // What it takes for a play to be believed — the same evidence bar LocalArbitrageAnalyzer
    // requires before it will call anything a goldmine.
    public const int MinCompsToBelieve = LocalArbitrageAnalyzer.GoldmineMinComps;
    public const int MinConfidenceToBelieve = LocalArbitrageAnalyzer.GoldmineMinConfidence;

    /// <summary>
    /// Whether a priced product has enough sold history behind it to appear on the board AT ALL.
    /// Local Deals can afford to show a two-comp estimate next to a listing the seller will go and
    /// inspect in person; a sweep that pushes an unfamiliar product at someone cannot. Two comps
    /// also make every number downstream unusable — the resale price, the break-even, and the price
    /// floor that decides whether a cheap listing is the product or a spare part for it.
    /// </summary>
    public static bool HasEnoughHistoryToShow(ResalePricing resale) =>
        resale.HasPrice && resale.SoldCompCount + resale.TerapeakCompCount >= MinCompsToBelieve;

    // Spec-shaped tokens: "104th", "3068w", "12v", "500gb". They describe an item, they don't
    // identify it, so they must never be mistaken for a model number.
    [GeneratedRegex(@"^\d+(?:\.\d+)?(?:th|ths|gh|mh|ph|w|watt|watts|v|volt|volts|kw|kwh|gb|tb|mb|mm|cm|in|inch|ft|lb|lbs|oz|ct|pcs|pc|pk|amp|amps|ah|mah|hz|khz|mhz|ghz|rpm|psi|k|s)$", RegexOptions.IgnoreCase)]
    private static partial Regex UnitTokenRegex();

    // Words that describe the sale rather than the product — dropped from the keyword a seller
    // takes hunting, so "Dyson V11 Torque Drive VERY CLEAN Tested" searches as "dyson v11 torque
    // drive" and not as somebody's listing copy.
    private static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "excellent", "great", "good", "mint", "clean", "nice", "condition", "works", "work",
        "complete", "bundle", "read", "look", "wow", "rare", "htf", "vintage", "refurbished",
        "preowned", "sealed", "box", "boxed", "warranty", "ship", "ships", "shipped", "usa",
        "fast", "free", "new", "used", "open", "tested", "working", "lot", "bulk", "wholesale",
    };

    // ── 1. Clustering: sold LISTINGS in, PRODUCTS out ────────────────────────────────────────

    /// <summary>
    /// The clustering signature for a comp title, plus the model-shaped token it was built from
    /// (null when there wasn't one). This is a grouping key, NOT a pricing identity — pricing
    /// identity stays <see cref="ProductNormalizer"/>/<see cref="ComparableMatcher"/>'s job, and
    /// every cluster that survives screening is re-priced through them by title anyway.
    ///
    /// Two shapes of key, because sold titles come in two shapes:
    ///   * a distinctive model token ("s19j", "rtx3080") keys on its own, so "Bitmain S19j" and
    ///     "Antminer S19j Pro" land in one cluster despite naming the brand differently;
    ///   * a short one ("m18", "v11") is ambiguous across brands, so it carries an anchor word
    ///     ("milwaukee|m18", "dyson|v11").
    /// A near-miss like RTX 3080 vs RTX 3080 Ti can share a key; the price-spread screen below and
    /// the per-product identity guard downstream are what keep that from moving a price.
    /// </summary>
    public static (string Key, string? ModelToken) ProductSignature(string? title)
    {
        var words = MarketplaceMatcher.Words(MarketplaceMatcher.Normalize(title));
        if (words.Count == 0) return ("", null);

        var model = ModelToken(words);
        var anchor = words.FirstOrDefault(w => w.Length >= 3 && w.All(char.IsLetter) && !NoiseWords.Contains(w));

        if (model is not null)
            return (model.Length >= 4 ? model : $"{anchor}|{model}", model);

        // No model designator anywhere in the title: key on the two most meaningful words and let
        // the loose-identity screen demand more evidence before this is priced.
        var significant = words.Where(w => w.Length >= 3 && w.All(char.IsLetter) && !NoiseWords.Contains(w)).Take(2).ToList();
        var key = significant.Count > 0 ? string.Join('|', significant) : string.Join(' ', words.Take(3));
        return (key, null);
    }

    // A model designator is either one mixed letter+digit token ("s19j", "a06b"), or an
    // alphabetic word followed by a bare number ("rtx 3080", "iphone 13", "m 18" never happens but
    // "airsense 10" does) — joined so both spellings of the same product collapse together.
    private static string? ModelToken(List<string> words)
    {
        foreach (var w in words)
        {
            if (w.Length < 3 || UnitTokenRegex().IsMatch(w)) continue;
            if (w.Any(char.IsDigit) && w.Any(char.IsLetter)) return w;
        }

        for (var i = 0; i < words.Count - 1; i++)
        {
            var alpha = words[i];
            var number = words[i + 1];
            if (alpha.Length < 2 || !alpha.All(char.IsLetter) || NoiseWords.Contains(alpha)) continue;
            if (number.Length is < 1 or > 5 || !number.All(char.IsDigit)) continue;
            return alpha + number;
        }

        return null;
    }

    /// <summary>
    /// Groups one probe's sold comps into candidate products with the stats the screen needs.
    /// Prices are per-unit (a lot of four at $400 is a $100 comp), matching what
    /// <see cref="MarketPriceEstimator"/> does — a lot's sticker price is not a product's price.
    /// </summary>
    public static List<JackpotCandidate> Cluster(
        IEnumerable<MarketplaceComparableResult> comps, string nicheId, string nicheLabel, string probe, DateTime nowUtc)
    {
        var groups = new Dictionary<string, List<(MarketplaceComparableResult Comp, decimal UnitPrice)>>(StringComparer.OrdinalIgnoreCase);
        var modelTokens = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var comp in comps)
        {
            if (string.IsNullOrWhiteSpace(comp.Title) || comp.SoldPrice <= 0) continue;
            var (key, model) = ProductSignature(comp.Title);
            if (string.IsNullOrWhiteSpace(key)) continue;

            if (!groups.TryGetValue(key, out var rows))
            {
                groups[key] = rows = [];
                modelTokens[key] = model;
                order.Add(key);
            }
            rows.Add((comp, Math.Round(comp.SoldPrice / Math.Max(1, comp.Quantity), 2)));
        }

        var candidates = new List<JackpotCandidate>(order.Count);
        foreach (var key in order)
        {
            var rows = groups[key];
            var prices = rows.Select(r => r.UnitPrice).OrderBy(p => p).ToList();
            var ages = rows.Where(r => r.Comp.SoldDate.HasValue)
                .Select(r => (int)Math.Max(0, (nowUtc - r.Comp.SoldDate!.Value).TotalDays))
                .ToList();

            var candidate = new JackpotCandidate
            {
                NicheId = nicheId, NicheLabel = nicheLabel, Probe = probe,
                Key = key,
                LookupTitle = PickLookupTitle(rows.Select(r => r.Comp.Title), modelTokens[key]),
                ImageUrl = rows.Select(r => r.Comp.ImageUrl).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)) ?? "",
                CompCount = rows.Count,
                RecentCompCount = ages.Count(a => a <= RecentDays),
                NewestCompAgeDays = ages.Count > 0 ? ages.Min() : null,
                MedianSold = Math.Round(MarketplacePricingCalculator.Median(prices), 2),
                LowSold = prices[0],
                HighSold = prices[^1],
                LooseIdentity = modelTokens[key] is null,
            };
            candidate.Score = ScoreCandidate(candidate);
            candidates.Add(candidate);
        }

        return candidates;
    }

    // The shortest title that still names the model and says enough to price against: sold titles
    // for the same product range from "Dyson V11" to "🔥DYSON V11 TORQUE DRIVE VACUUM FREE SHIP🔥",
    // and the lean one makes both a better comp query and a better keyword to go hunting with.
    private static string PickLookupTitle(IEnumerable<string> titles, string? modelToken)
    {
        var all = titles.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
        if (all.Count == 0) return "";

        bool NamesModel(string t) => modelToken is null ||
            MarketplaceMatcher.Normalize(t).Replace(" ", "").Contains(modelToken, StringComparison.OrdinalIgnoreCase);

        var usable = all
            .Where(t => NamesModel(t) && MarketplaceMatcher.ImportantWords(MarketplaceMatcher.Normalize(t)).Count >= 3)
            .OrderBy(t => t.Length)
            .ToList();

        return usable.Count > 0 ? usable[0] : all.OrderByDescending(t => t.Length).First();
    }

    // Ordering heuristic for "which of these products is worth a real lookup first" — dollars on
    // the table, discounted by how much history backs it and whether any of it is recent. Never
    // shown to the seller and never part of a price.
    private static double ScoreCandidate(JackpotCandidate c) =>
        (double)c.MedianSold * (Math.Min(c.CompCount, 10) / 10.0) * (c.RecentCompCount > 0 ? 1.0 : 0.6);

    // ── 2. Screening: what is not worth pricing, and why ─────────────────────────────────────

    /// <summary>
    /// Whether a candidate deserves a real (paid-for) price lookup. The reason is returned rather
    /// than swallowed — "we skipped 40 products" with no explanation is how a sweep silently
    /// becomes a random keyword generator.
    /// </summary>
    public static (bool Keep, string? Reason) Screen(JackpotCandidate candidate, NormalizedProduct identity)
    {
        if (identity.IsAccessoryListing)
            return (false, "the comps are accessories, not the product");

        if (identity.Quantity > 1)
            return (false, "sold as a multi-unit lot, so there's no single-item price");

        var disqualifying = identity.NegativeKeywords
            .Where(k => k is "broken" or "parts" or "for repair" or "empty box" or "lot")
            .ToList();
        if (disqualifying.Count > 0)
            return (false, $"the comps are {string.Join('/', disqualifying)} listings");

        var required = candidate.LooseIdentity ? MinCompsForLooseIdentity : MinCompsToPrice;
        if (candidate.CompCount < required)
            return (false, $"only {candidate.CompCount} sold comp{(candidate.CompCount == 1 ? "" : "s")}");

        if (candidate.MedianSold < MinMedianSold)
            return (false, $"sells for {candidate.MedianSold:C0} — too little to clear fees and shipping");

        // Dated comps that are all old means the demand has been and gone. No dates at all is a
        // gap in the data, not evidence of staleness, so it passes here and is penalised later by
        // ConfidenceScoringService's recency component instead.
        if (candidate.NewestCompAgeDays is not null && candidate.RecentCompCount == 0)
            return (false, $"nothing has sold in {RecentDays} days");

        if (candidate.LowSold > 0 && candidate.HighSold > candidate.LowSold * MaxPriceSpreadRatio)
            return (false, $"comps run {candidate.LowSold:C0}–{candidate.HighSold:C0} — too wide to be one product");

        return (true, null);
    }

    /// <summary>
    /// The keyword to go hunting with: the front of the product's title, stripped of listing copy
    /// and capped short enough that a classifieds search actually returns something.
    /// </summary>
    public static string ShoppingQuery(string? lookupTitle, int maxWords = 5)
    {
        var words = MarketplaceMatcher.Words(MarketplaceMatcher.Normalize(lookupTitle))
            .Where(w => w.Length > 1 && !NoiseWords.Contains(w))
            .Take(Math.Max(1, maxWords))
            .ToList();
        return words.Count > 0 ? string.Join(' ', words) : (lookupTitle ?? "").Trim();
    }

    // ── The buy-side identity guard ──────────────────────────────────────────────────────────
    // Search any marketplace for "Roomba Combo 10 Max" under $118 and what comes back is filters,
    // mop pads and roller brushes — priced like a bargain, and not the machine. Booking one of
    // those as a $97 flip is exactly the kind of number this feature must never print.

    // Phrases that mean "this fits the product" rather than "this is the product".
    private static readonly string[] CompatibilityMarkers =
        ["fits", "compatible", "replacement", "replaces", "aftermarket", "for use with"];

    // The same claim written as a bare verb — "Cooling Fan 4 pin fit Bitmain Antminer S19". Matched
    // as a WORD rather than as a substring, because "benefit " ends in "fit " and a substring test
    // here would reject half the listings on eBay.
    private static readonly string[] CompatibilityWords = ["fit", "fitment", "fitting"];

    // Component nouns. A listing that names one of these when the product itself doesn't is a
    // listing FOR that component — "Dirty Water Tank to iRobot Roomba Combo 10 Max" names the same
    // brand and model number as the machine and is a $40 plastic tank. Compared against the
    // product's own title rather than used as a blocklist, so a product that legitimately ships
    // with a dock or a battery isn't rejected for saying so.
    private static readonly string[] ComponentNouns =
    [
        "filter", "filters", "brush", "brushes", "pad", "pads", "bag", "bags", "belt", "blade",
        "blades", "tray", "tank", "bin", "battery", "batteries", "charger", "cord", "cable",
        "adapter", "adaptor", "bracket", "mount", "wheel", "wheels", "tire", "tires", "hose",
        "nozzle", "remote", "dock", "base", "lid", "door", "gasket", "seal", "roller", "motor",
        "pump", "screen", "bezel", "knob", "handle", "bumper", "sensor", "board", "pcb", "psu",
        "cover", "case", "sleeve", "strap", "band", "clip", "bracket", "screw", "screws",
        // Found by pointing the auction sniper at a real market: a keyword search for a $148 miner
        // returns fans, fan-speed controllers, fan-simulator plugs and rubber standoffs, all of them
        // naming the brand AND the model number, all of them $15, and every one of them priced as a
        // spectacular flip until it is rejected here.
        "fan", "fans", "controller", "simulator", "emulator", "standoff", "standoffs",
        "hashboard", "hashboards", "riser", "shroud", "duct", "harness", "supply",
    ];

    // Words that mean the listing is a SERVICE, a licence or a job rather than a thing in a box.
    // A live keyword search on eBay returns these mixed in with the real items, and they are the
    // most dangerous rows on a sourcing board: "ASIC Miner Hosting Europe — Antminer S21" costs
    // $1.00, names the brand and the model exactly, and cannot be resold at all. Compared against
    // the product's own title like the component nouns above, so a seller who really does flip
    // service plans isn't locked out of their own market.
    private static readonly string[] ServiceWords =
    [
        "hosting", "hosted", "rental", "rent", "lease", "firmware", "overclock", "unlock",
        "repair", "service", "servicing", "installation", "consultation", "tutorial", "ebook",
    ];

    // Fraction of the sold-comp floor below which a listing is not credibly the same item. Set
    // deliberately low — a genuine local bargain at a quarter of the going rate still passes — but
    // an accessory or a stripped/broken unit almost never clears it.
    public const decimal SupplyPriceFloorFraction = 0.25m;

    /// <summary>
    /// The words a listing must actually contain to be this product: the leading brand/name word
    /// plus every model number in it. Spec tokens ("104th") are excluded — they vary between
    /// listings of the same item — and so are bare words, since "vacuum" identifies nothing.
    /// </summary>
    public static List<string> IdentityTokens(string? lookupTitle)
    {
        var words = MarketplaceMatcher.Words(MarketplaceMatcher.Normalize(lookupTitle));
        var tokens = new List<string>();

        var anchor = words.FirstOrDefault(w => w.Length >= 3 && w.All(char.IsLetter) && !NoiseWords.Contains(w));
        if (anchor is not null) tokens.Add(anchor);

        foreach (var w in words)
        {
            if (UnitTokenRegex().IsMatch(w) || !w.Any(char.IsDigit)) continue;
            if (w.Length < 2 || tokens.Contains(w)) continue;
            tokens.Add(w);
        }

        return tokens;
    }

    /// <summary>The lowest price at which a listing is still credibly this product.</summary>
    public static decimal SupplyPriceFloor(ResalePricing resale)
    {
        var floor = resale.QuickSale is > 0 ? resale.QuickSale!.Value : resale.Median ?? 0m;
        return Math.Round(floor * SupplyPriceFloorFraction, 2);
    }

    /// <summary>
    /// Whether a listing found on the buy side is credibly the product being priced. Five ways it
    /// isn't, all of them things a plain keyword search returns every single time: it's an accessory
    /// or a broken/parts unit (<see cref="ProductNormalizer"/>'s own signals), it says it merely
    /// FITS the product, it names a component the product itself doesn't, it doesn't name the model,
    /// or it's priced far under what the product ever sells for. The reason comes back so a dropped
    /// listing is logged rather than silently vanishing.
    /// </summary>
    public static (bool Plausible, string? Reason) IsPlausibleSupply(
        string? supplyTitle, decimal buyPrice, NormalizedProduct supplyIdentity,
        string? productTitle, decimal priceFloor)
    {
        var normalized = MarketplaceMatcher.Normalize(supplyTitle);
        if (normalized.Length == 0) return (false, "no title");

        if (supplyIdentity.IsAccessoryListing) return (false, "accessory listing");

        var disqualifying = supplyIdentity.NegativeKeywords
            .Where(k => k is "parts" or "broken" or "for repair" or "empty box" or "case" or "cover"
                          or "manual" or "accessory" or "compatible" or "replica" or "lot")
            .ToList();
        if (disqualifying.Count > 0) return (false, string.Join('/', disqualifying));

        if (CompatibilityMarkers.Any(m => normalized.Contains(m, StringComparison.Ordinal)))
            return (false, "sold as a fit-for accessory");

        var identityTokens = IdentityTokens(productTitle);

        // "Filters for iRobot Roomba" — a listing for the product itself doesn't say "for <brand>".
        if (identityTokens.Count > 0 && normalized.Contains($"for {identityTokens[0]}", StringComparison.Ordinal))
            return (false, $"listed as an item for a {identityTokens[0]}, not the {identityTokens[0]}");

        var words = MarketplaceMatcher.Words(normalized);

        if (CompatibilityWords.Any(w => words.Contains(w, StringComparer.Ordinal)))
            return (false, "sold as a fit-for accessory");

        var missing = identityTokens.Where(t => !words.Contains(t, StringComparer.Ordinal)).ToList();
        if (missing.Count > 0) return (false, $"doesn't name {string.Join('/', missing)}");

        var productWords = MarketplaceMatcher.Words(MarketplaceMatcher.Normalize(productTitle));
        var component = ComponentNouns.FirstOrDefault(n =>
            words.Contains(n, StringComparer.Ordinal) && !productWords.Contains(n, StringComparer.Ordinal));
        if (component is not null) return (false, $"a {component}, not the product");

        var service = ServiceWords.FirstOrDefault(w =>
            words.Contains(w, StringComparer.Ordinal) && !productWords.Contains(w, StringComparer.Ordinal));
        if (service is not null) return (false, $"a {service} listing, not an item you can resell");

        if (priceFloor > 0 && buyPrice < priceFloor)
            return (false, $"{buyPrice:C} is below the {priceFloor:C} floor for a real one");

        return (true, null);
    }

    /// <summary>
    /// Cross-checks the two independent reads of the same product's sold history: the cluster the
    /// sweep built from one probe (several comps, one model signature) and the per-product lookup
    /// that priced it. A big disagreement means one of them matched a different product — a $150
    /// robot vacuum priced at $504 off a keyword-tier match — and neither number can be stood
    /// behind, so the play is dropped rather than shown with a caveat.
    /// </summary>
    public static bool EstimateAgreesWithSweep(decimal sweepMedian, ResalePricing resale, decimal maxRatio = 2m)
    {
        var estimate = resale.ExpectedSale is > 0 ? resale.ExpectedSale!.Value : resale.Median ?? 0m;
        if (sweepMedian <= 0 || estimate <= 0) return true;   // nothing to cross-check against

        var ratio = Math.Max(sweepMedian, estimate) / Math.Min(sweepMedian, estimate);
        return ratio <= maxRatio;
    }

    /// <summary>
    /// An eBay Buy It Now listing as a supply listing, so buying on eBay is costed by exactly the
    /// same path as buying on Craigslist (see <see cref="LocalArbitrageAnalyzer.Build"/>) instead
    /// of a second, subtly different profit calculation. Shipping the buyer pays is part of what
    /// acquiring it costs, so it's folded into the price.
    /// </summary>
    public static LocalSupplyListing AsSupplyListing(EbayOpportunityItem item) => new()
    {
        Source = "ebay", SourceLabel = "eBay Buy It Now",
        ItemId = item.Url, Title = item.Title, Url = item.Url, ImageUrl = item.ImageUrl,
        // The headline price and the shipping kept apart, the way EbaySupplySource states them: the
        // analyzer adds the two into a delivered cost itself, and a row that folds them silently has
        // no way left to say whether shipping was KNOWN. That distinction now decides whether the
        // money columns are shown at all, so a folded price read as "shipping unknown" and threw
        // away the profit on a listing whose shipping was stated on the page.
        Price = item.Price > 0 ? item.Price : null,
        PurchaseShippingCost = item.ShippingStated ? item.ShippingCost : null,
        FreeShipping = item.ShippingStated && item.ShippingCost == 0m,
        PriceText = item.ShippingCost > 0 ? $"{item.Price:C} + {item.ShippingCost:C} shipping" : $"{item.Price:C}",
        Location = string.IsNullOrWhiteSpace(item.SellerUsername) ? "" : $"seller {item.SellerUsername}",
    };

    // ── 3. The money ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The highest price that still breaks even after eBay's fees and shipping — profit at a cost
    /// basis of zero, since every dollar paid comes straight off the profit. Negative when the item
    /// cannot carry its own fees at any price, which is a real answer.
    /// </summary>
    public decimal BreakEvenBuyPrice(ResalePricing resale, FeeProfile fees)
    {
        var expected = resale.ExpectedSale is > 0 ? resale.ExpectedSale!.Value : resale.Median ?? 0m;
        if (expected <= 0) return 0m;

        var atZeroCost = profitCalc.Calculate(
            supplierUnitCost: 0m, quantity: 1, expectedSalePrice: expected,
            quickSalePrice: resale.QuickSale ?? expected,
            buyerPaidShipping: resale.AvgCompShipping, fees: fees,
            actualShippingCostOverride: resale.AvgCompShipping > 0 ? resale.AvgCompShipping : null);

        return atZeroCost.NetProfitPerUnit;
    }

    /// <summary>
    /// The price to pay for this to be a jackpot rather than merely a profit: the stricter of
    /// "clears <see cref="LocalArbitrageAnalyzer.GoldmineProfit"/> in cash" and "returns
    /// <see cref="LocalArbitrageAnalyzer.GoldmineRoiPercent"/> on the money". Zero when no price
    /// clears that bar. Truncated, never rounded up — this is a number someone negotiates against.
    /// </summary>
    public static decimal TargetBuyPrice(decimal breakEvenBuyPrice)
    {
        if (breakEvenBuyPrice <= 0) return 0m;

        var byRoi = breakEvenBuyPrice / (1m + LocalArbitrageAnalyzer.GoldmineRoiPercent / 100m);
        var byProfit = breakEvenBuyPrice - LocalArbitrageAnalyzer.GoldmineProfit;
        var target = Math.Min(byRoi, byProfit);
        return target <= 0 ? 0m : Math.Floor(target * 100m) / 100m;
    }

    /// <summary>
    /// One play: the product, what it resells for, what to pay, and the best of whatever supply was
    /// actually found. Options must already be priced (they come from
    /// <see cref="LocalArbitrageAnalyzer.Build"/>), so nothing here re-does fee arithmetic.
    /// </summary>
    public JackpotPlay BuildPlay(
        JackpotCandidate candidate, ResalePricing? resale, IEnumerable<JackpotSourceOption> options, FeeProfile fees)
    {
        var play = new JackpotPlay
        {
            Product = candidate.LookupTitle,
            NicheId = candidate.NicheId, NicheLabel = candidate.NicheLabel, Probe = candidate.Probe,
            ImageUrl = candidate.ImageUrl,
            SearchQuery = ShoppingQuery(candidate.LookupTitle),
        };

        if (resale is null || !resale.HasPrice)
        {
            play.Tier = "no_data";
            play.TierNote = "No eBay sold history could be matched to this product.";
            play.WhereToLook = "";
            play.WatchRefusal = "No sold history behind this — there's no price to watch for.";
            return play;
        }

        play.PricedAs = resale.LookupTitle;
        play.EbayResaleMedian = resale.Median;
        play.EbayExpectedSale = resale.ExpectedSale;
        play.EbayQuickSale = resale.QuickSale;
        play.ResaleSource = LocalArbitrageAnalyzer.SourceLabel(resale.SoldCompCount, resale.TerapeakCompCount);
        play.SoldCompCount = resale.SoldCompCount;
        play.TerapeakCompCount = resale.TerapeakCompCount;
        play.ConfidenceScore = resale.ConfidenceScore;
        play.ConfidenceLevel = resale.ConfidenceLevel;
        play.LiquidityScore = resale.LiquidityScore;
        play.LiquidityLevel = resale.LiquidityLevel;
        play.OpportunityScore = resale.OpportunityScore;
        play.DisagreementMessage = resale.DisagreementMessage;
        play.EstimatedMonthlySales = resale.EstimatedMonthlySales;

        var breakEven = BreakEvenBuyPrice(resale, fees);
        play.MaxBuyPrice = Math.Round(breakEven, 2);
        play.TargetBuyPrice = TargetBuyPrice(breakEven);
        play.ProfitAtTarget = Math.Round(Math.Max(0m, breakEven - play.TargetBuyPrice), 2);

        // Best money first, cheapest buy as the tie-break; an option we couldn't price sorts last
        // rather than being dropped, same rule the local ranking uses.
        play.Sources = options
            .OrderByDescending(o => o.NetProfit.HasValue)
            .ThenByDescending(o => o.NetProfit ?? 0m)
            .ThenBy(o => o.BuyPrice)
            .ToList();

        var best = play.Sources.FirstOrDefault();
        if (best?.NetProfit is not null)
        {
            play.NetProfit = best.NetProfit;
            play.RoiPercent = best.RoiPercent;
            play.MarginPercent = best.MarginPercent;
            play.BestBuyPrice = best.BuyPrice;
        }

        // How long the money stays spent, and what it earns per day of that. Measured on the live
        // buy where there is one, and on the target buy otherwise — a play with no supply yet still
        // has to be comparable against one that has.
        var moneyRoi = play.RoiPercent ?? (play.TargetBuyPrice > 0
            ? Math.Round(play.ProfitAtTarget / play.TargetBuyPrice * 100m, 1)
            : null);
        var speed = DaysToCashEstimator.Estimate(
            resale.EstimatedDaysToSell, resale.EstimatedMonthlySales,
            play.NetProfit ?? (play.ProfitAtTarget > 0 ? play.ProfitAtTarget : null), moneyRoi);

        play.DaysToSell = speed.DaysToSell;
        play.DaysToCash = speed.DaysToCash;
        play.ProfitPerDay = speed.ProfitPerDay;
        play.AnnualizedRoiPercent = speed.AnnualizedRoiPercent;
        play.SpeedTier = speed.SpeedTier;
        play.SpeedLabel = speed.SpeedLabel;
        play.SpeedNote = speed.Note;

        var (tier, note) = JudgePlay(best, resale.SoldCompCount + resale.TerapeakCompCount,
            resale.ConfidenceScore, resale.ConfidenceLevel, play.MaxBuyPrice, play.TargetBuyPrice);
        play.Tier = tier;
        play.TierNote = note;
        play.WhereToLook = WhereToLook(play.Sources, play.MaxBuyPrice, play.TargetBuyPrice);

        // Whether this play can be handed to Deal Radar and kept — decided here rather than in the
        // browser, so the button the board draws and the endpoint behind it are one rule.
        var (canWatch, watchRefusal) = PlayWatchBuilder.CanWatch(
            play.SearchQuery, play.TargetBuyPrice,
            resale.SoldCompCount + resale.TerapeakCompCount, resale.ConfidenceScore);
        play.CanWatch = canWatch;
        play.WatchRefusal = watchRefusal;

        return play;
    }

    /// <summary>
    /// The play's tier. Where live supply exists this is <see cref="LocalArbitrageAnalyzer"/>'s own
    /// verdict for that buy — including its comp-count and confidence gates, so thin evidence can
    /// never wear the jackpot badge however large the arithmetic. Where none exists, the play is
    /// only worth hunting if the resale evidence would have been believed anyway.
    /// </summary>
    public static (string Tier, string Note) JudgePlay(
        JackpotSourceOption? best, int compCount, int confidenceScore, string confidenceLevel,
        decimal maxBuyPrice, decimal targetBuyPrice)
    {
        if (maxBuyPrice <= 0)
            return ("pass", "Fees and shipping eat the whole sale price — no buy price makes this work.");

        if (best is not null)
        {
            var tier = best.Verdict switch
            {
                "goldmine" => "jackpot",
                "solid" => "strong",
                "thin" => "thin",
                "pass" => "pass",
                _ => "watch",
            };

            // The local ranking will call a real listing you can go and inspect "worth it" on three
            // comps. A product nobody asked about, surfaced by a sweep, has to bring more than that
            // before the board dresses it up — the arithmetic can be right and still be built on a
            // price nobody should bet on.
            var believable = compCount >= MinCompsToBelieve && confidenceScore >= MinConfidenceToBelieve;
            if (!believable && tier is "jackpot" or "strong")
                return ("thin", $"{best.SourceLabel}: {best.VerdictNote} Evidence is thin though — " +
                                $"{compCount} sold comp{(compCount == 1 ? "" : "s")}, {confidenceLevel}.");

            return (tier, $"{best.SourceLabel}: {best.VerdictNote}");
        }

        if (compCount >= MinCompsToBelieve && confidenceScore >= MinConfidenceToBelieve)
            return ("target", targetBuyPrice > 0
                ? $"Nothing for sale right now — buy under ${targetBuyPrice:0.##} and it clears " +
                  $"${LocalArbitrageAnalyzer.GoldmineProfit:0} net at {LocalArbitrageAnalyzer.GoldmineRoiPercent:0}% ROI."
                : $"Nothing for sale right now — break-even is ${maxBuyPrice:0.##}, so it only pays if you get it cheap.");

        // Name the actual blocker. "Not enough sold comps" in front of a product with twenty of them
        // reads as a bug, and the seller can't tell which weakness to go and fix.
        return ("watch", compCount < MinCompsToBelieve
            ? $"Only {compCount} sold comp{(compCount == 1 ? "" : "s")} — not enough history to bet money on yet."
            : $"{compCount} sold comps, but {confidenceLevel.ToLowerInvariant()} that they're the same " +
              $"product — treat ${maxBuyPrice:0.##} as indicative, not a number to bid on.");
    }

    // Where the thing can actually be bought, in one sentence. "Nothing found" is a real answer and
    // says so, rather than leaving an empty row to be read as a failed search.
    public static string WhereToLook(IReadOnlyList<JackpotSourceOption> options, decimal maxBuyPrice, decimal targetBuyPrice)
    {
        if (options.Count == 0)
            return targetBuyPrice > 0
                ? $"Nothing under ${maxBuyPrice:0.##} for sale on the sites checked — watch for one at ${targetBuyPrice:0.##} or less."
                : $"Nothing for sale on the sites checked — it only pays under ${maxBuyPrice:0.##}.";

        var byPlace = options
            .GroupBy(o => string.IsNullOrWhiteSpace(o.SourceLabel) ? o.Source : o.SourceLabel)
            .Select(g => $"{g.Count()} on {g.Key}");
        return $"For sale now: {string.Join(" · ", byPlace)}.";
    }

    /// <summary>
    /// Believable money first. Tier leads the sort rather than raw dollars, because the whole
    /// promise here is that the top of the board is the play most worth making — not the one with
    /// the biggest number attached to the weakest evidence.
    /// </summary>
    /// <param name="sort">
    /// <see cref="LocalArbitrageAnalyzer"/>'s sort names, so "fastest profit" means the same thing
    /// on this board as it does on the local ranking. The velocity modes still keep plays that
    /// can't be believed (pass / no data) at the bottom — a fast route to no money is not a play.
    /// </param>
    public static List<JackpotPlay> Rank(IEnumerable<JackpotPlay> plays, string? sort = null)
    {
        var ordered = plays.OrderBy(p => TierRank(p.Tier) >= TierRank("pass") ? 1 : 0);

        return LocalArbitrageAnalyzer.NormalizeSort(sort) switch
        {
            LocalArbitrageAnalyzer.SortByFastestCash => ordered
                .ThenBy(p => DaysToCashEstimator.SortableDaysToCash(p.DaysToCash))
                .ThenByDescending(p => p.NetProfit ?? p.ProfitAtTarget)
                .ThenBy(p => p.Product, StringComparer.OrdinalIgnoreCase)
                .ToList(),

            LocalArbitrageAnalyzer.SortByProfitPerDay => ordered
                .ThenByDescending(p => p.ProfitPerDay.HasValue)
                .ThenByDescending(p => p.ProfitPerDay ?? 0m)
                .ThenByDescending(p => p.NetProfit ?? p.ProfitAtTarget)
                .ThenBy(p => p.Product, StringComparer.OrdinalIgnoreCase)
                .ToList(),

            _ => ordered
                .ThenBy(p => TierRank(p.Tier))
                .ThenByDescending(p => p.NetProfit ?? p.ProfitAtTarget)
                .ThenByDescending(p => p.ConfidenceScore)
                .ThenBy(p => DaysToCashEstimator.SortableDaysToCash(p.DaysToCash))
                .ThenBy(p => p.Product, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    public static int TierRank(string tier) => tier switch
    {
        "jackpot" => 0, "strong" => 1, "target" => 2, "thin" => 3, "watch" => 4, "pass" => 5, _ => 6,
    };
}
