using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Turns completed sales into a shopping list: which products to go and buy again, in the order
/// that pays. Pure — no I/O, no clock of its own — so every rule below is testable and the board
/// is reproducible from the same sales.
/// </summary>
/// <remarks>
/// The one decision that shapes everything here is the ranking key. The obvious one is total profit,
/// and it is wrong: it ranks the single $900 pallet flip above the $60 part that sells four times a
/// month, and sends the seller looking for another pallet. The question this screen answers is what
/// to go and buy on a Saturday, so the ranking is <b>profit per month</b> — average profit per unit
/// multiplied by how fast that product actually moves. A line that makes $70 a unit twice a month
/// beats a line that makes $300 once a quarter, because the seller can do the first one again next
/// month and the second one is a story.
///
/// The refusals matter as much as the ranking:
///   * A sale with no recorded cost contributes nothing to profit. Not zero — nothing. Those lines
///     are moved to their own list with the proceeds sitting behind them, because entering one
///     number is the cheapest way to make this board honest.
///   * One sale is not a pattern, and there is no arithmetic that can make it one. Single-sale lines
///     are shown as a watch list and never ranked.
///   * Being sold out is not evidence against a product. A live listing that nobody buys is; an
///     empty shelf isn't, and treating the two the same way would bury exactly the lines this
///     screen exists to surface.
/// </remarks>
public static class RestockAnalyzer
{
    /// <summary>Orders needed before a line is ranked rather than merely reported.</summary>
    public const int MinOrdersToRank = 2;

    /// <summary>
    /// The shortest window a sales rate may be measured over — one month. Two sales a week apart is
    /// not eight a month, and a board that says it is will send a seller after eight of them. The
    /// floor is a whole month rather than thirty days so that two sales can never read as more than
    /// two a month, however close together they landed.
    /// </summary>
    public const decimal MinWindowDays = 30.44m;

    /// <summary>Days without a sale after which a proven line stops being proven and becomes a watch.</summary>
    public const int StaleDays = 180;

    /// <summary>Days a live, unsold listing has to sit before the ranking says so out loud.</summary>
    public const int SlowOnShelfDays = 90;

    /// <summary>Return rate that turns a line from a restock into a stop — with at least two returns behind it.</summary>
    public const decimal HighRefundRatePercent = 25m;

    private const decimal DaysPerMonth = MinWindowDays;

    public static RestockResult Analyze(
        IReadOnlyList<RestockSale> sales,
        IReadOnlyList<EbayListingSummary>? activeListings,
        DateTimeOffset now)
    {
        var result = new RestockResult();
        var usable = (sales ?? []).Where(s => s.Sale is not null && !string.IsNullOrWhiteSpace(s.Sale.Title)).ToList();
        result.Summary.SalesRead = usable.Count;

        if (usable.Count == 0)
        {
            result.Status = "no_sales";
            result.Honesty.Add("This board is built entirely from sales you have already made. Import your eBay orders on Money Made and it fills itself in.");
            return result;
        }

        var stock = activeListings is null ? null : CountStock(activeListings);
        result.StockStatus = stock is null ? "unavailable" : "read";

        var lines = new List<RestockLine>();
        foreach (var group in GroupByProduct(usable))
            lines.Add(BuildLine(group.Key, group.Value, stock, now));

        result.Summary.ProductLines = lines.Count;

        foreach (var line in lines)
        {
            switch (line.Verdict)
            {
                case "restock": result.Restock.Add(line); break;
                case "stop": result.Stop.Add(line); break;
                case "needs_cost": result.NeedsCost.Add(line); break;
                default: result.Watch.Add(line); break;
            }
        }

        // The shopping list leads with the money per month, because that is the question. Everything
        // else is ordered by how recently it happened — a stop list is read newest-first, and a watch
        // list of one-off sales has no rate to sort by.
        result.Restock = [.. result.Restock.OrderByDescending(l => l.ProfitPerMonth ?? 0m).ThenByDescending(l => l.UnitsSold)];
        result.Watch = [.. result.Watch.OrderByDescending(l => l.LastSoldUtc)];
        result.Stop = [.. result.Stop.OrderByDescending(l => l.UnitsSold).ThenByDescending(l => l.LastSoldUtc)];
        result.NeedsCost = [.. result.NeedsCost.OrderByDescending(l => l.ProceedsAwaitingCost)];

        Summarize(result, now);
        AddHonesty(result, stock is not null);
        return result;
    }

    // ── Grouping ─────────────────────────────────────────────────────────────────────────────
    // Keyed on the same product signature the sniper's watch list and the jackpot clusterer use, so
    // "Antminer S19j Pro 104TH" and "Bitmain S19j Pro" are one product with two sales rather than
    // two products with one each — which is the difference between a ranked restock and a pair of
    // unranked one-offs. SKU is deliberately NOT the key: plenty of sellers mint a unique SKU per
    // item, and grouping on that would shatter every line into single sales.

    private static Dictionary<string, List<RestockSale>> GroupByProduct(IReadOnlyList<RestockSale> sales)
    {
        var groups = new Dictionary<string, List<RestockSale>>(StringComparer.OrdinalIgnoreCase);

        foreach (var sale in sales)
        {
            var (key, _) = JackpotHunter.ProductSignature(sale.Sale.Title);
            if (string.IsNullOrWhiteSpace(key)) key = sale.Sale.Title.Trim().ToLowerInvariant();

            if (!groups.TryGetValue(key, out var rows)) groups[key] = rows = [];
            rows.Add(sale);
        }

        return groups;
    }

    private static Dictionary<string, (int Listings, int Units)> CountStock(IReadOnlyList<EbayListingSummary> listings)
    {
        var stock = new Dictionary<string, (int Listings, int Units)>(StringComparer.OrdinalIgnoreCase);

        foreach (var listing in listings)
        {
            if (string.IsNullOrWhiteSpace(listing.Title)) continue;
            // An ended listing is not stock. Anything eBay hasn't given a status is treated as live,
            // matching what the inventory scan does with the same feed.
            if (!(listing.Status is "ACTIVE" or "PUBLISHED" || string.IsNullOrWhiteSpace(listing.Status))) continue;

            var (key, _) = JackpotHunter.ProductSignature(listing.Title);
            if (string.IsNullOrWhiteSpace(key)) continue;

            var units = Math.Max(1, listing.Quantity);
            stock[key] = stock.TryGetValue(key, out var current)
                ? (current.Listings + 1, current.Units + units)
                : (1, units);
        }

        return stock;
    }

    // ── One product line ─────────────────────────────────────────────────────────────────────

    private static RestockLine BuildLine(
        string key, List<RestockSale> rows, Dictionary<string, (int Listings, int Units)>? stock, DateTimeOffset now)
    {
        var titles = rows.Select(r => r.Sale.Title).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        var lookupTitle = AuctionSniperAnalyzer.LeanestTitle(titles);

        var line = new RestockLine
        {
            Key = key,
            Title = lookupTitle,
            SearchQuery = JackpotHunter.ShoppingQuery(lookupTitle),
        };

        // A cancelled order never happened and a refunded one un-happened. Neither is demand and
        // neither is profit — but they are the whole point of the stop list, so they are counted
        // here and then kept out of every figure below.
        var sold = rows.Where(r => string.Equals(r.Sale.Status, "paid", StringComparison.OrdinalIgnoreCase)).ToList();
        var returned = rows.Except(sold).ToList();

        line.ReturnedUnits = returned.Sum(r => Math.Max(1, r.Sale.Quantity));
        line.UnitsSold = sold.Sum(r => Math.Max(1, r.Sale.Quantity));
        line.Orders = sold.Count;

        var attempted = line.UnitsSold + line.ReturnedUnits;
        line.RefundRatePercent = attempted > 0 ? Math.Round(line.ReturnedUnits * 100m / attempted, 1) : 0m;

        if (sold.Count == 0)
        {
            // Everything this product ever did was cancelled or refunded. There is no rate, no
            // margin and nothing to rank — only a warning, which is worth more than either.
            line.FirstSoldUtc = rows.Min(r => r.Sale.SoldUtc);
            line.LastSoldUtc = rows.Max(r => r.Sale.SoldUtc);
            line.DaysSinceLastSale = DaysBetween(line.LastSoldUtc, now);
            line.Verdict = "stop";
            line.Headline = line.ReturnedUnits == 1
                ? "The one you sold came back."
                : $"All {line.ReturnedUnits} you sold came back.";
            line.Cautions.Add("Refunded and cancelled orders are not counted as sales anywhere on this board.");
            ApplyStock(line, stock);
            return line;
        }

        line.FirstSoldUtc = sold.Min(r => r.Sale.SoldUtc);
        line.LastSoldUtc = sold.Max(r => r.Sale.SoldUtc);
        line.DaysSinceLastSale = DaysBetween(line.LastSoldUtc, now);

        line.Revenue = Math.Round(sold.Sum(r => r.Sale.GrossRevenue), 2);
        line.AverageSalePrice = Math.Round(line.Revenue / line.UnitsSold, 2);

        // One sale has no rate. Divided by the one-month floor it comes out as exactly one a month,
        // which is not a cautious estimate — it is a number invented out of a single event, and it
        // would print "$1,700 a month" on the same card that says one sale is not a pattern. Left
        // at zero, and the screen shows no rate at all rather than a made-up one.
        var rateIsMeasurable = sold.Count >= MinOrdersToRank;
        line.SalesPerMonth = rateIsMeasurable
            ? SalesPerMonth(line.UnitsSold, sold.Count, line.FirstSoldUtc, line.LastSoldUtc)
            : 0m;

        // ── The money, and only from the sales that can prove it ─────────────────────────────
        var priced = sold.Where(r => r.Sale.NetProfit.HasValue).ToList();
        line.UnitsWithKnownCost = priced.Sum(r => Math.Max(1, r.Sale.Quantity));
        line.UnitsAwaitingCost = line.UnitsSold - line.UnitsWithKnownCost;
        line.ProceedsAwaitingCost = Math.Round(sold.Where(r => !r.Sale.NetProfit.HasValue).Sum(r => r.Sale.NetProceeds), 2);

        ApplyStock(line, stock);

        if (priced.Count == 0)
        {
            line.Verdict = "needs_cost";
            line.Headline = $"Sold {Units(line.UnitsSold)} for {Money(line.Revenue)} — with no record of what they cost you.";
            line.Cautions.Add("Enter what you paid on Money Made and this line gets a profit, a rate and a place in the ranking.");
            return line;
        }

        line.NetProfit = Math.Round(priced.Sum(r => r.Sale.NetProfit!.Value), 2);
        line.AverageProfitPerUnit = Math.Round(line.NetProfit.Value / line.UnitsWithKnownCost, 2);

        var cost = priced.Sum(r => r.Sale.CostOfGoods ?? 0m);
        if (cost > 0)
        {
            line.AverageUnitCost = Math.Round(cost / line.UnitsWithKnownCost, 2);
            line.RoiPercent = Math.Round(line.NetProfit.Value / cost * 100m, 1);
        }

        if (rateIsMeasurable)
            line.ProfitPerMonth = Math.Round(line.AverageProfitPerUnit.Value * line.SalesPerMonth, 2);
        ApplyHoldingTime(line, priced);

        Judge(line);
        return line;
    }

    /// <summary>
    /// How many units a month this product actually moves, measured over its own selling window.
    /// </summary>
    /// <remarks>
    /// Two corrections, both of which stop the rate reading high:
    ///
    /// First to last sale is not the whole window. Four sales span three gaps, not four, so dividing
    /// four sales by that span invents a quarter of a sale — worst on exactly the thin histories
    /// where the ranking is most fragile. The span is extended by one average gap, which is the same
    /// as dividing by the gaps rather than the sales.
    ///
    /// And nothing is measured over less than a month. Two sales three days apart is a coincidence,
    /// not twenty a month, and there is no seller alive who can go and find twenty of them.
    /// </remarks>
    public static decimal SalesPerMonth(int units, int orders, DateTimeOffset first, DateTimeOffset last)
    {
        if (units <= 0) return 0m;

        var spanDays = (decimal)Math.Max(0d, (last - first).TotalDays);
        if (orders > 1 && spanDays > 0) spanDays = spanDays * orders / (orders - 1);

        var windowDays = Math.Max(spanDays, MinWindowDays);
        return Math.Round(units / (windowDays / DaysPerMonth), 2);
    }

    // How long the cash was tied up, and what that makes the return worth as a yearly rate. Only
    // ever from purchase dates the seller actually recorded — a holding period is not guessable
    // from a sale date, and an invented one would turn the sharpest figure on this board into the
    // most misleading. Median rather than mean: one item that sat in the garage for a year should
    // not decide what the line looks like.
    private static void ApplyHoldingTime(RestockLine line, List<RestockSale> priced)
    {
        var held = priced
            .Where(r => r.AcquiredUtc.HasValue && r.Sale.SoldUtc >= r.AcquiredUtc.Value)
            .Select(r => (r.Sale.SoldUtc - r.AcquiredUtc!.Value).TotalDays)
            .OrderBy(d => d)
            .ToList();

        if (held.Count == 0) return;

        line.UnitsWithHoldingTime = held.Count;
        var median = held.Count % 2 == 1
            ? held[held.Count / 2]
            : (held[held.Count / 2 - 1] + held[held.Count / 2]) / 2d;
        line.MedianDaysHeld = (int)Math.Round(median);

        // A same-day flip is a real thing and would divide by zero. Floored at a day, which
        // understates the rate rather than reporting an infinite one.
        var days = Math.Max(1m, (decimal)median);
        if (line.RoiPercent is decimal roi)
            line.AnnualReturnOnCashPercent = Math.Round(roi * 365m / days, 0);
    }

    private static void ApplyStock(RestockLine line, Dictionary<string, (int Listings, int Units)>? stock)
    {
        if (stock is null) return;

        var found = stock.TryGetValue(line.Key, out var live) ? live : (Listings: 0, Units: 0);
        line.ActiveListings = found.Listings;
        line.ActiveUnits = found.Units;
        line.SoldOut = found.Listings == 0;
    }

    // ── The verdict ──────────────────────────────────────────────────────────────────────────

    private static void Judge(RestockLine line)
    {
        var perUnit = line.AverageProfitPerUnit ?? 0m;

        // Losing money is the one finding that outranks everything else here, including a strong
        // rate. A product that sells briskly at a loss is the most expensive thing a reseller can
        // own, and it always looks like a good line until somebody works out the margin.
        if (perUnit <= 0m)
        {
            line.Verdict = "stop";
            line.Headline = perUnit == 0m
                ? $"Sold {Units(line.UnitsSold)} and made nothing on them."
                : $"Lost {Money(Math.Abs(perUnit))} a unit across {Sales(line.UnitsSold)}.";
            if (line.UnitsAwaitingCost > 0)
                line.Cautions.Add($"{line.UnitsAwaitingCost} more of these sold with no cost recorded, so this is measured on {line.UnitsWithKnownCost}.");
            return;
        }

        if (line.ReturnedUnits >= 2 && line.RefundRatePercent >= HighRefundRatePercent)
        {
            line.Verdict = "stop";
            line.Headline = $"{line.RefundRatePercent:0.#}% of these came back — {line.ReturnedUnits} of {line.UnitsSold + line.ReturnedUnits}.";
            line.Cautions.Add($"The {Sales(line.UnitsSold)} that stuck made {Money(perUnit)} each, which is why this one is easy to keep buying.");
            return;
        }

        if (line.Orders < MinOrdersToRank)
        {
            line.Verdict = "watch";
            line.Headline = $"Sold one, {Money(perUnit)} profit. One sale is not a pattern.";
            line.Cautions.Add("Sell another and this moves into the ranking with a real rate behind it.");
            AddSharedCautions(line);
            return;
        }

        if (line.DaysSinceLastSale > StaleDays)
        {
            line.Verdict = "watch";
            line.Headline = $"{Units(line.UnitsSold)} at {Money(perUnit)} each — but the last one sold {Months(line.DaysSinceLastSale)} ago.";
            line.Cautions.Add("Check what these go for now before buying more. Half a year is long enough for a market to move.");
            AddSharedCautions(line);
            return;
        }

        line.Verdict = "restock";
        line.Headline = $"{Money(line.ProfitPerMonth ?? 0m)} a month — {Units(line.UnitsSold)} at {Money(perUnit)} profit each, {line.SalesPerMonth:0.#} a month.";
        AddSharedCautions(line);

        if (line.SoldOut)
            line.Cautions.Add($"You have none listed. That is {Money(line.ProfitPerMonth ?? 0m)} a month you are not earning.");
        else if (line.ActiveListings > 0 && line.DaysSinceLastSale >= SlowOnShelfDays)
            line.Cautions.Add($"You have {line.ActiveListings} listed and none has sold in {line.DaysSinceLastSale} days — the market may be slower than this rate suggests.");
    }

    private static void AddSharedCautions(RestockLine line)
    {
        if (line.UnitsAwaitingCost > 0)
            line.Cautions.Add($"{line.UnitsAwaitingCost} of the {Units(line.UnitsSold)} sold have no cost recorded, so the profit here is measured on {line.UnitsWithKnownCost}.");

        if (line.ReturnedUnits > 0 && line.Verdict != "stop")
            line.Cautions.Add($"{line.ReturnedUnits} came back and {(line.ReturnedUnits == 1 ? "is" : "are")} not counted in any figure above.");

        if (line.AverageUnitCost is null)
            line.Cautions.Add("What you paid was recorded as zero, so there is no return-on-cash figure for this one.");
    }

    // ── The board ────────────────────────────────────────────────────────────────────────────

    private static void Summarize(RestockResult result, DateTimeOffset now)
    {
        var s = result.Summary;
        s.RankedLines = result.Restock.Count;
        s.ProvenMonthlyProfit = Math.Round(result.Restock.Sum(l => l.ProfitPerMonth ?? 0m), 2);

        var soldOut = result.Restock.Where(l => l.SoldOut).ToList();
        s.SoldOutLines = soldOut.Count;
        s.MonthlyProfitOffTheShelf = Math.Round(soldOut.Sum(l => l.ProfitPerMonth ?? 0m), 2);
        s.CashToRestockSoldOut = Math.Round(soldOut.Sum(l => l.AverageUnitCost ?? 0m), 2);

        var best = result.Restock.FirstOrDefault();
        if (best is not null && s.ProvenMonthlyProfit > 0)
        {
            s.TopLineTitle = best.Title;
            s.TopLineShareOfProfitPercent = Math.Round((best.ProfitPerMonth ?? 0m) / s.ProvenMonthlyProfit * 100m, 0);
        }

        s.LinesAwaitingCost = result.NeedsCost.Count;
        s.ProceedsAwaitingCost = Math.Round(
            result.NeedsCost.Sum(l => l.ProceedsAwaitingCost)
            + result.Restock.Sum(l => l.ProceedsAwaitingCost)
            + result.Watch.Sum(l => l.ProceedsAwaitingCost), 2);
    }

    private static void AddHonesty(RestockResult result, bool stockRead)
    {
        result.Honesty.Add("Every figure here comes from sales you have already made — eBay's own fee where eBay reported one, and the cost you entered yourself. Nothing on this page is a market forecast.");
        result.Honesty.Add($"A rate is never measured over less than one month, and a product needs {MinOrdersToRank} sales before it is ranked. One sale is shown, and labelled as one sale.");
        result.Honesty.Add("Sales with no recorded cost are not counted as profit anywhere — not as zero, not at all. They are listed separately with what they are worth once you enter the cost.");

        if (result.Summary.ProceedsAwaitingCost > 0)
            result.Honesty.Add($"{Money(result.Summary.ProceedsAwaitingCost)} of proceeds is sitting behind sales with no cost against them. That is the ranking's biggest blind spot and it takes one number each to fix.");

        result.Honesty.Add(stockRead
            ? "\"None listed\" is read from your live eBay listings, matched on the same product signature that groups these sales. A listing worded very differently from the one that sold may not match."
            : "Your live listings could not be read, so nothing here knows what you currently have in stock. Every other figure is unaffected.");

        result.Honesty.Add("This says what has sold for you before. It does not know what a market did last week, whether a supply dried up, or that a product was discontinued — check the price before you buy.");
    }

    // ── Wording ──────────────────────────────────────────────────────────────────────────────

    private static int DaysBetween(DateTimeOffset from, DateTimeOffset to) =>
        Math.Max(0, (int)Math.Floor((to - from).TotalDays));

    private static string Units(int units) => units.ToString();

    private static string Sales(int units) => units == 1 ? "1 sale" : $"{units} sales";

    private static string Months(int days) =>
        days >= 365 ? $"{days / 365} year{(days / 365 == 1 ? "" : "s")}" : $"{Math.Max(1, days / 30)} months";

    private static string Money(decimal value) =>
        value >= 100m || value <= -100m ? $"${value:N0}" : $"${value:0.00}";
}
