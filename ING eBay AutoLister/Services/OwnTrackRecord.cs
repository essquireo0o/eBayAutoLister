using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// What the seller has actually done with the thing on screen — their own completed sales of it and
/// the units of it they are already sitting on — turned into the one number a live bid needs: the
/// ceiling their own results imply, beside the one the comps imply.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this outranks the comps when it exists.</b> Every other figure on the WhatsNot card is a
/// model: a resale price estimated from strangers' sold listings, an eBay fee estimated from a fee
/// profile, a postage cost estimated from a rate book. The seller's own sale of the same product is
/// none of those things. <see cref="FlipProfit.NetProceeds"/> is money that arrived minus money that
/// left — eBay's real fee, the real label, the real refunds — so the average of it per unit IS this
/// seller's break-even all-in cost for this product, measured rather than derived.
/// </para>
/// <para>
/// <b>It is not a second opinion about money.</b> The ceiling here comes out of
/// <see cref="AuctionSniperAnalyzer.MaxBidDetail"/> and the walk-away line out of
/// <see cref="LiveBidAdvisor.BreakEvenBid"/> — the same two functions the badge above it uses, at
/// the same shipping, premium and target. Only the evidence underneath differs, and the card says
/// which is which. There is still exactly one function in this app that turns a break-even into a
/// maximum bid.
/// </para>
/// <para>
/// <b>What it refuses to do</b> is most of it. It will not price off a loose identity, off a single
/// sale, off sales whose postage was never recorded, or off refunded orders — and it never moves the
/// badge. It reports, and the seller decides; a screen that quietly re-rated the call on two of the
/// seller's own sales would be a screen that occasionally talks somebody out of a good lot on the
/// strength of the one they listed badly.
/// </para>
/// <para>Pure. No clock, no I/O — the caller reads the stores and passes the rows in.</para>
/// </remarks>
public static class OwnTrackRecord
{
    /// <summary>
    /// Sales needed before this seller's own prices are allowed to price a ceiling. The Restock
    /// board's bar, deliberately: one sale is not a pattern there and it is not one here, and two
    /// screens disagreeing about when the seller's history counts is worse than either bar.
    /// </summary>
    public const int MinOrdersToTrust = RestockAnalyzer.MinOrdersToRank;

    /// <summary>Past sales listed on the card. Enough to see the spread, few enough to glance at.</summary>
    public const int MaxRowsShown = 5;

    /// <summary>Days after which the seller's own last sale stops being a price and starts being a
    /// memory. It still prices the ceiling — it is their own evidence — but the card says so.</summary>
    public const int StaleRecordDays = 365;

    /// <summary>
    /// How far under the comps ceiling the seller's own ceiling has to fall before it is called out
    /// rather than merely shown. Under this the two agree, and a warning on every card is a warning
    /// nobody reads.
    /// </summary>
    public const decimal MaterialGapPercent = 10m;

    /// <summary>Deal-pipeline stages that mean "the cash has left and the item has not sold".</summary>
    private static readonly string[] HoldingStages = [DealStages.Bought, DealStages.Listed];

    // ── The facts ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The seller's history with the product named by <paramref name="title"/>.
    /// </summary>
    /// <param name="sales">
    /// The seller's completed sales, already costed — the exact rows the Restock board is built
    /// from, so "your sales of this" means the same set on both screens.
    /// </param>
    /// <param name="deals">The Deal Pipeline, for the units already bought and not yet sold.</param>
    public static OwnSalesEvidence Match(
        string? title, IReadOnlyList<RestockSale>? sales, IReadOnlyList<DealRecord>? deals, DateTimeOffset now)
    {
        var rows = sales ?? [];
        var (key, model) = JackpotHunter.ProductSignature(title);

        var evidence = new OwnSalesEvidence
        {
            Key = key,
            ModelToken = model ?? "",
            // No model designator in the live title means the key is two ordinary words —
            // "vintage|lot" matches a great deal that is not this item. Recorded here and refused
            // by Price below, rather than silently producing a ceiling off somebody's junk drawer.
            IdentityIsLoose = model is null,
            SalesRead = rows.Count(r => r.Sale is not null && !string.IsNullOrWhiteSpace(r.Sale.Title)),
        };

        if (key.Length == 0) return evidence;

        var matched = rows
            .Where(r => r.Sale is not null && !string.IsNullOrWhiteSpace(r.Sale.Title))
            .Where(r => string.Equals(JackpotHunter.ProductSignature(r.Sale.Title).Key, key, StringComparison.OrdinalIgnoreCase))
            .ToList();

        ApplyHeldStock(evidence, deals, key, now);

        if (matched.Count == 0) return evidence;

        // Refunded and cancelled orders are not sales. Counted on their own line, exactly as the
        // Restock board counts them — an item that came back twice is a fact about this product the
        // ceiling cannot see.
        evidence.ReturnedUnits = matched
            .Where(r => EarningsStore.NormalizeStatus(r.Sale.Status) != "paid")
            .Sum(r => Math.Max(1, r.Sale.Quantity));

        var paid = matched.Where(r => EarningsStore.NormalizeStatus(r.Sale.Status) == "paid").ToList();
        if (paid.Count == 0) return evidence;

        evidence.Orders = paid.Count;
        evidence.UnitsSold = paid.Sum(r => Math.Max(1, r.Sale.Quantity));

        var units = evidence.UnitsSold;
        evidence.AverageSalePrice = units > 0
            ? Math.Round(paid.Sum(r => r.Sale.Flip.SalePrice * Math.Max(1, r.Sale.Quantity)) / units, 2)
            : null;

        ApplyProceeds(evidence, paid);
        ApplyProfit(evidence, paid);
        ApplyHoldingTime(evidence, paid);

        var last = paid.Max(r => r.Sale.SoldUtc);
        evidence.LastSoldUtc = last;
        evidence.DaysSinceLastSale = (int)Math.Max(0d, Math.Floor((now - last).TotalDays));

        evidence.Sales = paid
            .OrderByDescending(r => r.Sale.SoldUtc)
            .Take(MaxRowsShown)
            .Select(r => Row(r, now))
            .ToList();

        return evidence;
    }

    /// <summary>
    /// The break-even the seller actually achieved, per unit.
    /// </summary>
    /// <remarks>
    /// Sales whose postage was never recorded are left out. Their proceeds are flattering by exactly
    /// whatever the label cost, and a flattered break-even is a raised ceiling — the one direction
    /// an error here can cost real money at a live auction. Left out, counted, and said.
    /// </remarks>
    private static void ApplyProceeds(OwnSalesEvidence evidence, List<RestockSale> paid)
    {
        var usable = paid.Where(r => !r.Sale.ShippingCostUnknown).ToList();
        evidence.UnitsMissingShippingCost = paid
            .Where(r => r.Sale.ShippingCostUnknown)
            .Sum(r => Math.Max(1, r.Sale.Quantity));

        var units = usable.Sum(r => Math.Max(1, r.Sale.Quantity));
        if (units <= 0) return;

        evidence.UnitsPricingProceeds = units;
        evidence.AverageNetProceeds = Math.Round(usable.Sum(r => r.Sale.NetProceeds) / units, 2);
    }

    // Profit needs the cost of goods, which is the one thing the seller has to have typed. Units
    // without it contribute nothing — not zero, nothing — and are reported so that typing one
    // number is visibly what turns this line into a profit figure.
    private static void ApplyProfit(OwnSalesEvidence evidence, List<RestockSale> paid)
    {
        var costed = paid.Where(r => r.Sale.NetProfit.HasValue && r.Sale.CostOfGoods.HasValue).ToList();
        evidence.UnitsWithKnownCost = costed.Sum(r => Math.Max(1, r.Sale.Quantity));
        evidence.UnitsAwaitingCost = evidence.UnitsSold - evidence.UnitsWithKnownCost;

        if (evidence.UnitsWithKnownCost <= 0) return;

        evidence.AverageNetProfit = Math.Round(costed.Sum(r => r.Sale.NetProfit!.Value) / evidence.UnitsWithKnownCost, 2);
        evidence.AverageUnitCost = Math.Round(costed.Sum(r => r.Sale.CostOfGoods!.Value) / evidence.UnitsWithKnownCost, 2);
    }

    // Median rather than mean, and only from purchase dates the seller actually recorded: one unit
    // that sat in the garage for a year should not decide what "sells in" says about the next one.
    private static void ApplyHoldingTime(OwnSalesEvidence evidence, List<RestockSale> paid)
    {
        var held = paid
            .Where(r => r.AcquiredUtc.HasValue && r.Sale.SoldUtc >= r.AcquiredUtc.Value)
            .Select(r => (r.Sale.SoldUtc - r.AcquiredUtc!.Value).TotalDays)
            .OrderBy(d => d)
            .ToList();

        if (held.Count == 0) return;

        var median = held.Count % 2 == 1
            ? held[held.Count / 2]
            : (held[held.Count / 2 - 1] + held[held.Count / 2]) / 2d;
        evidence.MedianDaysToSell = (int)Math.Round(median);
    }

    private static void ApplyHeldStock(
        OwnSalesEvidence evidence, IReadOnlyList<DealRecord>? deals, string key, DateTimeOffset now)
    {
        if (deals is null) return;

        var held = deals
            .Where(d => HoldingStages.Contains(d.Stage))
            .Where(d => !string.IsNullOrWhiteSpace(d.Title))
            .Where(d => string.Equals(JackpotHunter.ProductSignature(d.Title).Key, key, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (held.Count == 0) return;

        evidence.UnitsHeld = held.Sum(d => Math.Max(1, d.Quantity));
        evidence.CapitalHeld = Math.Round(
            held.Sum(d => (d.PurchasePrice ?? 0m) * Math.Max(1, d.Quantity) + d.PurchaseExtraCost), 2);

        var ages = held
            .Select(d => d.BoughtUtc ?? d.ListedUtc ?? d.CreatedUtc)
            .Where(at => at <= now)
            .Select(at => (int)Math.Max(0d, Math.Floor((now - at).TotalDays)))
            .ToList();
        if (ages.Count > 0) evidence.OldestHeldDays = ages.Max();
    }

    private static OwnSaleRow Row(RestockSale sale, DateTimeOffset now)
    {
        var quantity = Math.Max(1, sale.Sale.Quantity);
        return new OwnSaleRow
        {
            Title = sale.Sale.Title,
            SoldUtc = sale.Sale.SoldUtc,
            DaysAgo = (int)Math.Max(0d, Math.Floor((now - sale.Sale.SoldUtc).TotalDays)),
            Quantity = quantity,
            SalePrice = sale.Sale.Flip.SalePrice,
            NetProceeds = sale.Sale.ShippingCostUnknown ? null : Math.Round(sale.Sale.NetProceeds / quantity, 2),
            NetProfit = sale.Sale.NetProfit is decimal profit ? Math.Round(profit / quantity, 2) : null,
            DaysHeld = sale.AcquiredUtc is DateTimeOffset bought && sale.Sale.SoldUtc >= bought
                ? (int)Math.Max(0d, Math.Floor((sale.Sale.SoldUtc - bought).TotalDays))
                : null,
            ShippingCostUnknown = sale.Sale.ShippingCostUnknown,
        };
    }

    // ── The money ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The seller's record priced at this auction's terms, beside the ceiling the comps produced.
    /// </summary>
    /// <param name="compsMaxBid">
    /// The comps ceiling from the same card, for the comparison only. Zero when nothing priced the
    /// item — which is the case where the seller's own record becomes the only ceiling on screen.
    /// </param>
    public static LiveOwnHistory Price(
        OwnSalesEvidence? evidence, decimal shipping, decimal buyerFeePercent, decimal targetRoiPercent,
        decimal compsMaxBid, decimal? compsResale)
    {
        var e = evidence ?? new OwnSalesEvidence();
        var history = new LiveOwnHistory
        {
            Orders = e.Orders,
            UnitsSold = e.UnitsSold,
            AverageSalePrice = e.AverageSalePrice,
            AverageNetProceeds = e.AverageNetProceeds,
            AverageNetProfit = e.AverageNetProfit,
            AverageUnitCost = e.AverageUnitCost,
            MedianDaysToSell = e.MedianDaysToSell,
            DaysSinceLastSale = e.DaysSinceLastSale,
            ReturnedUnits = e.ReturnedUnits,
            UnitsHeld = e.UnitsHeld,
            CapitalHeld = e.CapitalHeld,
            OldestHeldDays = e.OldestHeldDays,
            IdentityIsLoose = e.IdentityIsLoose,
            Sales = e.Sales,
        };

        history.Verdict = e.Orders >= MinOrdersToTrust ? OwnTrackVerdicts.Proven
            : e.Orders == 1 ? OwnTrackVerdicts.Once
            : e.UnitsHeld > 0 ? OwnTrackVerdicts.Holding
            : OwnTrackVerdicts.None;

        ApplyCeiling(history, e, shipping, buyerFeePercent, targetRoiPercent, compsMaxBid);
        history.Headline = Headline(history, e);
        history.Notes.AddRange(Notes(history, e, compsResale));

        return history;
    }

    private static void ApplyCeiling(
        LiveOwnHistory history, OwnSalesEvidence e,
        decimal shipping, decimal buyerFeePercent, decimal targetRoiPercent, decimal compsMaxBid)
    {
        // Three refusals, in the order that matters: the wrong product, too little of it, and a
        // break-even that is only high because a postage cost is missing.
        if (e.IdentityIsLoose || e.Orders < MinOrdersToTrust) return;
        if (e.AverageNetProceeds is not decimal proceeds) return;

        // The seller's own sales did not cover their own fees and postage. Not a thin ceiling — no
        // ceiling, and the most important thing this panel will ever say about a product.
        if (proceeds <= 0m)
        {
            history.CeilingComparison =
                $"Your own sales of these did not cover their own fees and postage — {proceeds:C} per unit before " +
                "the goods were paid for. There is nothing to buy another one with, at any price.";
            return;
        }

        var (maxBid, boundBy) = AuctionSniperAnalyzer.MaxBidDetail(proceeds, shipping, targetRoiPercent, buyerFeePercent);
        history.OwnMaxBid = maxBid;
        history.OwnCeilingBoundBy = boundBy;
        history.OwnBreakEvenBid = LiveBidAdvisor.BreakEvenBid(proceeds, buyerFeePercent, shipping);

        if (maxBid <= 0m)
        {
            history.CeilingComparison =
                $"Your own sales clear {proceeds:C} each after eBay's cut and the postage — not enough left over " +
                "to be worth buying another one at any price.";
            return;
        }

        if (compsMaxBid <= 0m)
        {
            history.OwnIsTheOnlyCeiling = true;
            history.CeilingComparison =
                $"eBay's sold history couldn't price this, but you have sold {Units(e.UnitsSold)} yourself for " +
                $"{proceeds:C} net each. On your own record the ceiling is {maxBid:C} — your evidence, not the market's.";
            return;
        }

        history.CeilingGap = Math.Round(maxBid - compsMaxBid, 2);
        var gap = Math.Abs(history.CeilingGap.Value);
        var material = compsMaxBid > 0m && gap / compsMaxBid * 100m >= MaterialGapPercent;

        if (history.CeilingGap < 0m && material)
        {
            history.CeilingIsLower = true;
            history.CeilingComparison =
                $"Your own {Units(e.UnitsSold)} cleared {proceeds:C} each, which puts your ceiling at {maxBid:C} — " +
                $"{gap:C} under the {compsMaxBid:C} the comps allow. The badge is the market's number; this one is yours.";
        }
        else if (history.CeilingGap > 0m && material)
        {
            history.CeilingComparison =
                $"You do better with these than the comps suggest: {proceeds:C} net each puts your own ceiling at " +
                $"{maxBid:C}, {gap:C} above the {compsMaxBid:C} on the badge. The badge is still the one with " +
                "sold history behind it — yours is worth bidding to only if you can repeat what you did.";
        }
        else
        {
            history.CeilingComparison =
                $"Your own sales agree with the comps — {proceeds:C} net each puts your ceiling at {maxBid:C} " +
                $"against the badge's {compsMaxBid:C}.";
        }
    }

    /// <summary>One sentence for what this seller has done with this product.</summary>
    public static string Headline(LiveOwnHistory history, OwnSalesEvidence e)
    {
        if (history.Verdict == OwnTrackVerdicts.None)
        {
            if (e.SalesRead == 0)
                return "No sales of your own are recorded yet, so there is nothing here to check this against. Import your eBay orders on Money Made and this fills itself in.";
            return "You have never sold one of these. The ceiling above is the market's record, not yours.";
        }

        if (history.Verdict == OwnTrackVerdicts.Holding)
        {
            return $"You have never sold one, and you already have {Units(e.UnitsHeld)} of these bought and unsold" +
                   (e.OldestHeldDays is int days ? $" — the oldest {days} days ago." : ".");
        }

        var got = e.AverageSalePrice is decimal price and > 0m ? $" at {price:C0} each" : "";
        var net = e.AverageNetProfit is decimal profit
            ? $", {profit:C0} net"
            : (e.UnitsAwaitingCost > 0 ? ", none of them with a cost recorded" : "");
        var speed = e.MedianDaysToSell is int held ? $", gone in about {held} day{(held == 1 ? "" : "s")}" : "";

        if (history.Verdict == OwnTrackVerdicts.Once)
            return $"You have sold exactly one of these{got}{net}{speed}. One sale is a data point, not a pattern.";

        return $"You have sold {Units(e.UnitsSold)} of these{got}{net}{speed}.";
    }

    /// <summary>Everything the headline and the ceiling cannot say, each on its own line.</summary>
    public static List<string> Notes(LiveOwnHistory history, OwnSalesEvidence e, decimal? compsResale)
    {
        var notes = new List<string>();

        if (e.IdentityIsLoose && e.Orders > 0)
        {
            notes.Add("There is no model number in what you typed, so these were matched on ordinary words — " +
                      "close enough to show you, not close enough to price a ceiling off. Type the model and " +
                      "press Price it again.");
        }

        if (history.Verdict == OwnTrackVerdicts.Once && !e.IdentityIsLoose)
            notes.Add("A second sale of this product is what turns your own record into a ceiling on this card.");

        if (e.UnitsMissingShippingCost > 0)
        {
            notes.Add($"{Units(e.UnitsMissingShippingCost)} of your sales had no postage cost recorded and " +
                      $"{(e.UnitsMissingShippingCost == 1 ? "is" : "are")} left out of the figure above — proceeds " +
                      "with the label missing read higher than they were.");
        }

        if (e.UnitsAwaitingCost > 0 && e.UnitsWithKnownCost > 0)
            notes.Add($"{Units(e.UnitsAwaitingCost)} of them have no cost recorded, so the net above is from the other {e.UnitsWithKnownCost}.");

        if (e.ReturnedUnits > 0)
        {
            notes.Add($"{Units(e.ReturnedUnits)} came back to you and {(e.ReturnedUnits == 1 ? "is" : "are")} " +
                      "not counted in anything above.");
        }

        if (e.DaysSinceLastSale is int since && since > StaleRecordDays)
            notes.Add($"Your last one sold {since / 30} months ago — that is your price from then, not from now.");

        if (compsResale is decimal comps and > 0m && e.AverageSalePrice is decimal mine and > 0m)
        {
            var gap = Math.Round(mine - comps, 2);
            if (Math.Abs(gap) / comps * 100m >= MaterialGapPercent)
            {
                notes.Add(gap < 0m
                    ? $"Your own listings got {mine:C} for these against the comps' {comps:C}. The gap is condition, photos, timing or the fact that the comps are somebody else's listing — but it is what you actually get."
                    : $"Your own listings got {mine:C} for these against the comps' {comps:C}, so the resale figure above is the conservative one for you.");
            }
        }

        return notes;
    }

    /// <summary>
    /// The lines that belong on the card's warning list rather than inside the record's own panel —
    /// facts about the seller's position that change the answer to "should I bid on this".
    /// </summary>
    public static List<string> Warnings(LiveOwnHistory? history)
    {
        var warnings = new List<string>();
        if (history is null) return warnings;

        if (history.UnitsHeld > 0)
        {
            var capital = history.CapitalHeld > 0m ? $"{history.CapitalHeld:C0} of your money is in " : "";
            var oldest = history.OldestHeldDays is int days
                ? $", the oldest bought {days} days ago and still not sold"
                : "";
            warnings.Add($"You already have {Units(history.UnitsHeld)} of these bought and unsold — {capital}" +
                         $"this product before tonight{oldest}. Another one competes with your own.");
        }

        if (history.Verdict == OwnTrackVerdicts.Proven && history.AverageNetProceeds is decimal net && net <= 0m)
        {
            warnings.Add($"Every one of these you sold left {net:C} per unit after eBay's cut and the postage, " +
                         "before you had even paid for the goods. You lose money on this product.");
        }

        if (history.CeilingIsLower && history.OwnMaxBid > 0m)
        {
            warnings.Add($"On what you actually got for the last ones, the ceiling is {history.OwnMaxBid:C} rather " +
                         "than the badge's. The badge is priced off the market; you are not the market.");
        }

        if (history.OwnIsTheOnlyCeiling && history.OwnMaxBid > 0m)
        {
            warnings.Add($"Nothing on eBay priced this, but you have sold {Units(history.UnitsSold)} — on your own " +
                         $"sales the most to bid is {history.OwnMaxBid:C}.");
        }

        return warnings;
    }

    private static string Units(int count) => count == 1 ? "one" : count.ToString();
}
