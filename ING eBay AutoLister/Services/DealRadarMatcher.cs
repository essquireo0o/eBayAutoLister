using System.Globalization;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The bar. Given a scan the local-arbitrage board would have rendered, decides which rows are
/// worth interrupting a person for — and writes the one sentence a desktop notification has room to
/// say it in.
/// </summary>
/// <remarks>
/// <para>
/// Pure: a scan in, alerts out, no clock of its own and nothing written anywhere. Every number on an
/// alert is copied from the row the board produced. Nothing is recomputed here, because a
/// notification that quotes a different profit from the screen it links to is worse than no
/// notification.
/// </para>
/// <para>
/// Three gates a row passes before the seller's evening is interrupted, in order of how much money
/// each one saves:
/// </para>
/// <list type="number">
///   <item><b>Is there a real number at all?</b> A row the app refused to value (a truck against
///     tow-hitch comps — see <see cref="ResaleValuation"/>) has no profit to clear a bar with. It is
///     never alerted on, at any threshold. The board can afford to show it with dashes and a search
///     link; a toast cannot.</item>
///   <item><b>Does the board itself believe it?</b> Only <c>goldmine</c> and <c>solid</c> verdicts
///     qualify, and by default only <c>confident</c> evidence. A 700% ROI off one loose comp is the
///     single most common way this app could publish a number it can't stand behind, and the one
///     place it must not is a notification, which has no room for the caveat the board prints.</item>
///   <item><b>Is it the seller's kind of deal?</b> Their profit floor, their ROI floor, their cash
///     ceiling, their driving distance. All four ANDed, all four theirs.</item>
/// </list>
/// <para>
/// And then the rule that decides whether the feature is usable at all: <b>once per listing, ever</b>.
/// A classified sits on craigslist for two weeks; a watch scanning every three hours would find the
/// same post 112 times. Dedupe is on <see cref="ItemKey"/>, held by the store across restarts.
/// </para>
/// </remarks>
public static class DealRadarMatcher
{
    /// <summary>Verdicts worth waking someone for. Anything thinner stays on the board.</summary>
    private static readonly string[] AlertableVerdicts = ["goldmine", "solid"];

    /// <summary>Headlines get unreadable past this; a Windows balloon truncates around here anyway.</summary>
    public const int MaxTitleChars = 46;

    /// <summary>
    /// What makes the same post, found on scan after scan, one alert. Source-scoped because item ids
    /// are only unique within a site — craigslist's 7738471234 and a deal feed's are different things.
    /// </summary>
    /// <remarks>
    /// Falls back to the URL and then to the title, because an id is the one field a scraped listing
    /// can plausibly be missing — and a missing key would make every scan re-alert the whole board,
    /// which is the failure that gets notifications turned off for good.
    /// </remarks>
    public static string ItemKey(LocalArbitrageOpportunity row)
    {
        var source = (row.Source ?? "").Trim().ToLowerInvariant();
        var id = (row.ItemId ?? "").Trim();
        if (id.Length == 0) id = (row.Url ?? "").Trim();
        if (id.Length == 0) id = (row.Title ?? "").Trim().ToLowerInvariant();
        return id.Length == 0 ? "" : $"{source}:{id}";
    }

    /// <summary>
    /// Every row from one scan that clears this watch's bar and hasn't been alerted before, newest
    /// money first.
    /// </summary>
    /// <param name="seenKeys">
    /// Item keys this watch has already fired on. Passed in rather than read here so the whole
    /// decision stays pure and the store owns what "already seen" means.
    /// </param>
    public static List<DealAlert> Match(
        DealWatch watch, LocalArbitrageResult scan, DateTimeOffset now, ISet<string>? seenKeys = null)
    {
        if (watch is null || scan?.Items is null) return [];

        var alerts = new List<DealAlert>();
        var keysThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in scan.Items.OrderByDescending(r => r.NetProfit ?? 0m))
        {
            if (!Qualifies(watch, row)) continue;

            var key = ItemKey(row);
            if (key.Length == 0) continue;
            // Twice within one scan too: the same post can arrive from two sources, and the second
            // copy is the same drive to the same address.
            if (seenKeys?.Contains(key) == true || !keysThisRun.Add(key)) continue;

            alerts.Add(BuildAlert(watch, row, key, now));
        }

        return alerts;
    }

    /// <summary>Whether one row clears this watch's bar. The three gates, in cost order.</summary>
    public static bool Qualifies(DealWatch watch, LocalArbitrageOpportunity row)
    {
        if (watch is null || row is null) return false;

        // ── Gate 1: is there a number ────────────────────────────────────────
        // A refused valuation has no resale price by design, and no threshold can be applied to a
        // dash. The board shows these with a sold-listings link; the radar stays quiet about them.
        if (row.Valuation is { Status: ValuationStatuses.Manual }) return false;
        // And a price the model guessed is not a number to wake somebody up for. It is a fine
        // answer on a board the seller is reading — it is badged, dimmed and filterable there —
        // but a push notification saying "$400 profit, three miles away" carries none of that, so
        // the radar only ever fires on sold history. See the AI pass in FindLocalArbitrageAsync.
        if (row.Valuation is { Status: ValuationStatuses.AiEstimate }
            || row.EvidenceTier == LocalArbitrageEvidence.Ai) return false;
        if (row.NetProfit is not { } profit) return false;
        if (row.EbayExpectedSale is not > 0 && row.EbayResaleMedian is not > 0) return false;

        // ── Gate 2: does the board believe it ────────────────────────────────
        if (!AlertableVerdicts.Contains(row.Verdict, StringComparer.Ordinal)) return false;
        if (watch.RequireConfidentEvidence && row.EvidenceTier != LocalArbitrageEvidence.Confident) return false;
        // Even with the evidence gate off, a row priced off nothing at all never fires: "no" evidence
        // means the comps didn't identify the product, not that they were merely thin.
        if (row.EvidenceTier == LocalArbitrageEvidence.None) return false;

        // ── Gate 3: is it this seller's kind of deal ─────────────────────────
        if (profit < watch.MinNetProfit) return false;
        if (watch.MinRoiPercent > 0 && (row.RoiPercent ?? 0m) < watch.MinRoiPercent) return false;

        // What actually leaves the wallet: the till price on a retail row (tax included), the ask on
        // a private-party one. A cash ceiling that ignored sales tax would clear a deal the seller
        // can't afford by exactly the tax.
        var cost = row.BuyCostAllIn ?? row.LocalAsk;
        if (watch.MaxAsk > 0 && cost > watch.MaxAsk) return false;

        // An unstated distance passes: the search was already bounded by the radius, and dropping
        // every craigslist row that didn't publish a mileage would empty the feature.
        if (watch.MaxDistanceMiles > 0 && row.DistanceMiles is { } miles && miles > watch.MaxDistanceMiles)
            return false;

        return true;
    }

    private static DealAlert BuildAlert(
        DealWatch watch, LocalArbitrageOpportunity row, string key, DateTimeOffset now)
    {
        var alert = new DealAlert
        {
            WatchId = watch.Id,
            WatchName = watch.Name,
            ItemKey = key,
            Title = row.Title ?? "",
            Url = row.Url ?? "",
            ImageUrl = row.ImageUrl ?? "",
            Source = row.Source ?? "",
            SourceLabel = row.SourceLabel ?? "",
            Location = row.Location ?? "",
            DistanceMiles = row.DistanceMiles,
            CategoryLabel = row.CategoryLabel ?? "",
            LocalAsk = row.BuyCostAllIn ?? row.LocalAsk,
            ResalePrice = row.EbayExpectedSale ?? row.EbayResaleMedian,
            NetProfit = row.NetProfit,
            RoiPercent = row.RoiPercent,
            MarginPercent = row.MarginPercent,
            MaxBuyPrice = row.MaxBuyPrice,
            DaysToCash = row.DaysToCash,
            // What the price was actually computed from, not what the search returned — the same
            // number the board's evidence column shows.
            CompCount = row.PricedCompCount > 0 ? row.PricedCompCount : row.SoldCompCount + row.TerapeakCompCount,
            EvidenceTier = row.EvidenceTier ?? "",
            EvidenceNote = row.EvidenceNote ?? "",
            Verdict = row.Verdict ?? "",
            FoundUtc = now,
        };

        alert.Headline = Headline(alert);
        return alert;
    }

    /// <summary>
    /// "$400 Antminer S19 · 3 mi away → resells ~$700 · $210 profit, 52% margin".
    /// </summary>
    /// <remarks>
    /// Written as a sentence rather than assembled from labelled fields because the place it has to
    /// work is a Windows balloon and a phone-shaped notification: two lines, no table, no columns.
    /// Every clause is dropped rather than faked when the row didn't say — an unknown distance is
    /// simply absent, never "0 mi away".
    /// </remarks>
    public static string Headline(DealAlert alert)
    {
        if (alert is null) return "";

        var buy = $"{Money(alert.LocalAsk)} {Shorten(alert.Title)}".Trim();
        var where = alert.DistanceMiles is { } miles && miles > 0
            ? $" · {FormatMiles(miles)} away"
            : alert.Location is { Length: > 0 } place ? $" · {place}" : "";

        var resale = alert.ResalePrice is { } price && price > 0 ? $" → resells ~{Money(price)}" : "";

        // Profit and margin together: profit alone reads small on a cheap flip and margin alone reads
        // enormous on one. The pair is the only honest one-line summary of a deal.
        var money = alert.NetProfit is { } profit
            ? alert.MarginPercent is { } margin && margin > 0
                ? $" · {Money(profit)} profit, {Percent(margin)} margin"
                : $" · {Money(profit)} profit"
            : "";

        return (buy + where + resale + money).Trim();
    }

    /// <summary>
    /// One line for a run that found several. Sent instead of a stack of balloons — five notifications
    /// in five seconds is how a person learns to dismiss them without reading.
    /// </summary>
    public static string SummaryHeadline(IReadOnlyList<DealAlert> alerts)
    {
        if (alerts is null || alerts.Count == 0) return "";
        if (alerts.Count == 1) return alerts[0].Headline;

        var total = alerts.Sum(a => a.NetProfit ?? 0m);
        var best = alerts.OrderByDescending(a => a.NetProfit ?? 0m).First();
        return $"{alerts.Count} new deals worth {Money(total)} — best: {best.Headline}";
    }

    /// <summary>"S19s under $500 · 2 new" — the balloon's title line.</summary>
    public static string NotificationTitle(DealWatch watch, int count)
    {
        var name = string.IsNullOrWhiteSpace(watch?.Name) ? "Deal Radar" : watch!.Name.Trim();
        return count > 1 ? $"{name} · {count} new deals" : name;
    }

    private static string Shorten(string title)
    {
        var text = (title ?? "").Trim();
        if (text.Length <= MaxTitleChars) return text;
        // Cut on a word boundary where there is one nearby, so the line doesn't end mid-model-number.
        var cut = text.LastIndexOf(' ', Math.Min(MaxTitleChars, text.Length - 1));
        return (cut > MaxTitleChars / 2 ? text[..cut] : text[..MaxTitleChars]).TrimEnd() + "…";
    }

    /// <summary>Whole dollars. Cents in a notification are noise on a number that is a forecast anyway.</summary>
    private static string Money(decimal value) =>
        Math.Round(value, 0, MidpointRounding.AwayFromZero).ToString("C0", CultureInfo.CurrentCulture);

    private static string Percent(decimal value) =>
        Math.Round(value, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>"3 mi" / "0.4 mi" — a sub-mile distance rounded to zero would read as a typo.</summary>
    private static string FormatMiles(double miles) =>
        miles < 1
            ? miles.ToString("0.#", CultureInfo.InvariantCulture) + " mi"
            : Math.Round(miles).ToString("0", CultureInfo.InvariantCulture) + " mi";
}
