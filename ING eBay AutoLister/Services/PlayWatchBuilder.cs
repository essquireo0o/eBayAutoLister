using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Turns one play off the cross-category sweep into a saved Deal Radar watch.
/// </summary>
/// <remarks>
/// <para>
/// A roll costs minutes across four systems and its most useful output is the tier nobody can act
/// on: <c>target</c> — "nothing is for sale right now, buy under $X and it clears the bar". The
/// board then says, in those words, that the target price is "still worth watching for", and until
/// now offered no way to watch for it. The next roll threw the whole thing away.
/// </para>
/// <para>
/// <see cref="DealRadarStore"/> is already the thing that keeps looking. A watch takes a keyword, a
/// price ceiling and a profit bar; a play has all three. This is the mapping, and it is deliberately
/// the whole of it — no new scanning, no new pricing, no second definition of a good buy.
/// </para>
/// <para>
/// Three refusals, and every one of them makes the feature smaller. A watch fires a desktop
/// notification at whatever hour the listing appears, with room for one sentence and no room to
/// explain a caveat — so a play the board itself would not bet on must never become one. That is the
/// same rule <see cref="DealWatch.RequireConfidentEvidence"/> exists for, applied one level earlier.
/// </para>
/// </remarks>
public static class PlayWatchBuilder
{
    /// <summary>How much of the product title the watch's name carries before it's cut short.</summary>
    public const int MaxProductNameLength = 44;

    /// <summary>
    /// Whether a play may be watched, and the sentence saying why not. Called twice: once by
    /// <see cref="JackpotHunter.BuildPlay"/> so the board knows whether to draw the button, and
    /// again on the way in, because the board is a courtesy and this is the rule.
    /// </summary>
    public static (bool Allowed, string? Refusal) CanWatch(
        string? searchQuery, decimal targetBuyPrice, int compCount, int confidenceScore)
    {
        if (string.IsNullOrWhiteSpace(searchQuery))
            return (false, "There's no keyword to search for.");

        // Not "watch at break-even": paying the break-even price earns nothing, so a watch set there
        // wakes the seller up for a flip worth $0. Where no price clears the jackpot bar there is
        // genuinely nothing to watch for, and saying so is the honest answer.
        if (targetBuyPrice <= 0)
            return (false, "No buy price makes this a deal — even free it barely clears its fees, so there's nothing to watch for.");

        if (compCount < JackpotHunter.MinCompsToBelieve)
            return (false, $"Only {compCount} sold comp{(compCount == 1 ? "" : "s")} behind this price — too thin to wake you up for.");

        if (confidenceScore < JackpotHunter.MinConfidenceToBelieve)
            return (false, "Low confidence that the comps are all the same product — too thin to wake you up for.");

        return (true, null);
    }

    /// <summary>
    /// The watch, or the refusal. Never both, and never a watch built from numbers that failed
    /// <see cref="CanWatch"/>.
    /// </summary>
    public static (DealWatchRequest? Watch, string? Refusal) Build(PlayWatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var compCount = request.SoldCompCount + request.TerapeakCompCount;
        var (allowed, refusal) = CanWatch(request.SearchQuery, request.TargetBuyPrice, compCount, request.ConfidenceScore);
        if (!allowed) return (null, refusal);

        return (new DealWatchRequest
        {
            Name = WatchName(request.Product, request.TargetBuyPrice),
            Query = CollapseSpace(request.SearchQuery),
            // The sweep's niches are its own id space (see CategorySweep), not resale categories, so
            // there is nothing honest to map here — the watch prices what it finds the same way the
            // roll did, by title.
            CategoryId = ResaleCategoryCatalog.AnythingId,

            ZipCode = (request.ZipCode ?? "").Trim(),
            RadiusMiles = request.RadiusMiles,
            Sources = (request.Sources ?? "").Trim(),
            CraigslistSite = (request.CraigslistSite ?? "").Trim(),

            // The ceiling is the play's own target price — the number the board printed. Anything
            // dearer is not the deal the seller asked to be told about.
            MaxAsk = request.TargetBuyPrice,

            // The bar is the app's own jackpot bar, which is where the target price came from, so a
            // watch created here can never fire on something the board that created it would call a
            // pass. Stricter than a hand-made watch's defaults ($75 / 40%) on purpose: this one was
            // not typed by anybody, and it runs unattended.
            MinNetProfit = LocalArbitrageAnalyzer.GoldmineProfit,
            MinRoiPercent = LocalArbitrageAnalyzer.GoldmineRoiPercent,
            RequireConfidentEvidence = true,

            IntervalMinutes = DealRadarClock.DefaultIntervalMinutes,
            Enabled = true,
            NotifyDesktop = true,
        }, null);
    }

    /// <summary>
    /// What the watch is called on the Deal Radar card: the product and the price that makes it
    /// worth buying. The price is floored, never rounded — a name reading "under $58" against a
    /// target of $57.94 is a name that talks the seller into overpaying.
    /// </summary>
    public static string WatchName(string? product, decimal targetBuyPrice)
    {
        var name = Shorten(CollapseSpace(product));
        var ceiling = Math.Floor(targetBuyPrice);
        return name.Length == 0 ? $"Under {ceiling:C0}" : $"{name} under {ceiling:C0}";
    }

    /// <summary>
    /// Whether two watches are looking for the same thing. Pressing the button on the same product
    /// two rolls running must not spend one of the twelve watch slots twice.
    /// </summary>
    public static bool SameSearch(string? a, string? b) =>
        string.Equals(CollapseSpace(a), CollapseSpace(b), StringComparison.OrdinalIgnoreCase);

    private static string Shorten(string text)
    {
        if (text.Length <= MaxProductNameLength) return text;

        var cut = text[..MaxProductNameLength];
        var lastSpace = cut.LastIndexOf(' ');
        return (lastSpace > 0 ? cut[..lastSpace] : cut).TrimEnd() + "…";
    }

    private static string CollapseSpace(string? text) =>
        string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
