using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// What one venue's live supply says about what an item fetches there. Prices are per-unit and
/// already filtered for relevance by the caller — this type is the boundary between "scraping and
/// matching", which is I/O, and the money comparison, which is not.
/// </summary>
public sealed class LocalVenueEvidence
{
    public string Venue { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>ok | not_connected | session_expired | error | no_data</summary>
    public string Status { get; set; } = "ok";
    public string? Error { get; set; }
    public string SearchUrl { get; set; } = "";
    /// <summary>Asking prices of the listings that actually matched the item.</summary>
    public List<decimal> Prices { get; set; } = [];
    /// <summary>How many results the search returned before the relevance filter, for the note.</summary>
    public int RawResultCount { get; set; }
}

/// <summary>
/// Answers "where does this item net the most?" instead of assuming eBay.
/// </summary>
/// <remarks>
/// <para>
/// Every screen in this app prices against eBay sold history, and then eBay takes 13.25% plus a
/// per-order fee plus the shipping label out of that price. A local cash sale takes none of it. So
/// the venue with the highest price and the venue that hands over the most money are frequently
/// different venues, and nothing here has ever said so — a seller could be handing eBay $28 a flip
/// for a buyer who was standing in the next town.
/// </para>
/// <para>
/// The comparison is only worth as much as its honesty, so three rules are enforced rather than
/// left to the UI:
/// </para>
/// <list type="bullet">
/// <item>An eBay figure comes from what buyers <b>paid</b>; a Craigslist or Marketplace figure comes
/// from what sellers are <b>asking</b>. Asks are haircut by <see cref="LocalAskRealization"/> and
/// every row carries its <c>EvidenceKind</c>, so the two are never silently equated.</item>
/// <item>A venue with fewer than <see cref="MinLocalSamples"/> matching listings is shown but is
/// not <c>Rankable</c> — it can never be declared the winner off one hopeful listing.</item>
/// <item>A win has to be worth the move: see <see cref="IsMaterial"/>. Two dollars is not a reason
/// to leave the marketplace the seller's whole workflow already lives on.</item>
/// </list>
/// <para>
/// No new fee math lives here. Each venue is costed by cloning the seller's own
/// <see cref="FeeProfile"/> and overriding what that venue actually changes, then running it
/// through the same <see cref="ProfitCalculator"/> as every other screen — so the eBay column on
/// this page cannot disagree with the eBay number anywhere else in the app.
/// </para>
/// </remarks>
public sealed class WhereToSellAnalyzer(ProfitCalculator profitCalc, CrossListingFeeProfile crossFees)
{
    public const string Ebay = "ebay";
    public const string Facebook = "facebook";
    public const string Craigslist = "craigslist";
    public const string Mercari = "mercari";

    /// <summary>
    /// What a local classified ask actually realises. Local listings are opening positions —
    /// haggling is the norm on Marketplace and Craigslist in a way it is not on a Buy It Now — so
    /// the observed median is marked down before it is allowed to compete with an eBay sold price.
    /// Deliberately mild: 10% understates nothing the seller can check for themselves, and a
    /// bigger haircut would be a guess dressed up as caution.
    /// </summary>
    public const decimal LocalAskRealization = 0.90m;

    /// <summary>Below this many matching listings, a venue is described but never crowned.</summary>
    public const int MinLocalSamples = 3;

    /// <summary>A win has to clear both of these to be worth changing where you sell.</summary>
    public const decimal MaterialGapDollars = 5m;
    public const decimal MaterialGapPercent = 4m;

    /// <summary>
    /// One venue's fixed characteristics. Fee rates come from <see cref="CrossListingFeeProfile"/>
    /// (published standard rates, already documented as estimates there) except eBay's, which comes
    /// from the seller's own configured profile.
    /// </summary>
    public sealed record VenueSpec(
        string Key, string Label, string SaleMode, string SaleModeLabel,
        bool SellerShips, bool ReturnsApply, int CashPipelineDays, string CreateUrl, string FeeNote);

    public const string ShippedMode = "shipped";
    public const string LocalPickupMode = "local_pickup";

    public IReadOnlyList<VenueSpec> Venues =>
    [
        new(Ebay, "eBay", ShippedMode, "Shipped to the buyer", true, true,
            DaysToCashEstimator.PipelineDays, "https://www.ebay.com/sl/sell",
            "Your configured eBay fee profile — final value fee, per-order fee, ad rate and the reserves you set."),

        new(Facebook, "Facebook Marketplace", LocalPickupMode, "Local pickup, cash on handoff", false, false,
            0, "https://www.facebook.com/marketplace/create/item",
            $"Local pickup pays no selling fee at all — the whole price is yours. Shipping it instead costs about {crossFees.FacebookFeePercent:0.##}% plus the label."),

        new(Craigslist, "Craigslist", LocalPickupMode, "Local pickup, cash on handoff", false, false,
            0, "https://post.craigslist.org/",
            "Craigslist takes nothing from a private-party sale, and the money is cash in hand rather than a payout."),

        new(Mercari, "Mercari", ShippedMode, "Shipped to the buyer", true, true,
            DaysToCashEstimator.PipelineDays, "https://www.mercari.com/sell/",
            $"Estimate: {crossFees.MercariFeePercent:0.##}% seller selling fee — Mercari moved the service fee to the buyer in 2024. Confirm against your current seller terms."),
    ];

    // ── Fee profile per venue ────────────────────────────────────────────────────────────────
    // A venue changes four things about a sale and nothing else: who takes a cut and how much, who
    // pays to ship it, whether there is a returns window, and whether a processor is involved. Each
    // is overridden explicitly on a clone of the seller's own profile, so their packaging, handling
    // and testing assumptions travel to every venue rather than quietly resetting to zero.
    public FeeProfile FeesFor(VenueSpec spec, FeeProfile ebayFees)
    {
        if (spec.Key == Ebay) return ebayFees;

        var fees = ebayFees.Clone();
        (fees.EbayFinalValueFeePercent, fees.EbayFinalValueFeeFixed) = spec.Key switch
        {
            Facebook => (0m, 0m),                                              // local pickup
            Craigslist => (0m, 0m),
            Mercari => (crossFees.MercariFeePercent, crossFees.MercariFeeFixed),
            _ => (ebayFees.EbayFinalValueFeePercent, ebayFees.EbayFinalValueFeeFixed),
        };

        // Promoted Listings is an eBay ad product; it does not follow the item anywhere else.
        fees.PromotedListingRatePercent = 0m;
        // Cash in hand has no processor. A shipped off-eBay sale does, but it is charged by that
        // site's own schedule rather than the seller's eBay one, so it is not carried across.
        fees.PaymentProcessingPercent = 0m;

        if (!spec.SellerShips)
        {
            // The buyer carries it to their car. No label, no box, no filler.
            fees.DefaultShippingCost = 0m;
            fees.DefaultPackagingCost = 0m;
        }
        if (!spec.ReturnsApply) fees.ReturnReservePercent = 0m;

        fees.Sanitize();
        return fees;
    }

    /// <summary>
    /// The whole comparison. <paramref name="ebay"/> is the resale pricing the comps pipeline
    /// already produced for this item; <paramref name="local"/> is whatever the other venues' live
    /// supply said. Both may be missing — a report with nothing to compare says so rather than
    /// inventing a venue to recommend.
    /// </summary>
    public WhereToSellReport Build(
        string query, ResalePricing? ebay, IReadOnlyList<LocalVenueEvidence> local,
        FeeProfile ebayFees, decimal? unitCost, string zip = "", int radiusMiles = 0)
    {
        var cost = unitCost is > 0m ? unitCost.Value : 0m;
        var report = new WhereToSellReport
        {
            Query = query, ZipCode = zip, RadiusMiles = radiusMiles,
            UnitCost = unitCost, HasCostBasis = cost > 0m,
        };

        var byVenue = local.ToDictionary(e => e.Venue, StringComparer.OrdinalIgnoreCase);
        var priced = new List<(VenueOutlook Row, FeeProfile Fees)>();

        foreach (var spec in Venues)
        {
            var fees = FeesFor(spec, ebayFees);
            var row = spec.Key == Ebay
                ? BuildEbay(spec, ebay, fees, cost)
                : BuildOffEbay(spec, byVenue.GetValueOrDefault(spec.Key), fees, cost);
            priced.Add((row, fees));
        }

        var rows = priced.Select(p => p.Row).ToList();
        var ebayRow = rows.First(r => r.Venue == Ebay);
        report.EbayNet = ebayRow.Rankable ? ebayRow.NetProceeds : null;

        // The price every other venue has to fetch to match eBay. Fee arithmetic only, so it is
        // answerable even for a venue this app cannot see any supply on at all — which is exactly
        // the Mercari case, and is a far more useful thing to show there than a blank.
        foreach (var (row, fees) in priced.Where(p => p.Row.Venue != Ebay))
            ApplyPriceToBeatEbay(row, report.EbayNet, fees);

        Rank(rows, report.EbayNet);
        report.Venues = rows;

        var winner = rows.FirstOrDefault(r => r.Rankable && r.NetProceeds is not null);
        if (winner is not null)
        {
            report.BestVenue = winner.Venue;
            report.BestVenueLabel = winner.VenueLabel;
            report.BestNet = winner.NetProceeds;
        }

        (report.Verdict, report.Headline, report.Subhead, report.ExtraVsEbay) =
            Describe(winner, ebayRow, report.EbayNet, report.HasCostBasis);

        AddWarnings(report, rows, ebay);
        return report;
    }

    // ── eBay: the baseline, priced off what buyers actually paid ─────────────────────────────
    private VenueOutlook BuildEbay(VenueSpec spec, ResalePricing? resale, FeeProfile fees, decimal cost)
    {
        var row = NewRow(spec);
        row.Status = "ok";

        if (resale is null || !resale.HasPrice)
        {
            row.Verdict = "no_data";
            row.EvidenceKind = "none";
            row.EvidenceLabel = "No sold history";
            row.Note = "No eBay sold history matched this item, so there is no eBay figure to compare against.";
            row.PriceBasis = "Nothing matched in the sold-comps database or Terapeak.";
            return row;
        }

        var price = resale.ExpectedSale is > 0 ? resale.ExpectedSale.Value : resale.Median!.Value;
        // Buyer-paid shipping is booked as revenue AND as cost, exactly as LocalArbitrageAnalyzer
        // books it — eBay charges its final value fee on it, and the label costs the seller the
        // same money. Booking it on one side only inflates or deflates every number below.
        var shipping = resale.AvgCompShipping;

        row.SampleCount = resale.SoldCompCount + resale.TerapeakCompCount;
        row.EvidenceKind = "sold";
        row.EvidenceLabel = $"{row.SampleCount} sold comp{(row.SampleCount == 1 ? "" : "s")}";
        row.ObservedMedian = resale.Median;
        row.PriceBasis = $"What buyers actually paid: {row.EvidenceLabel} priced as \"{resale.LookupTitle}\".";
        row.Confidence = resale.ConfidenceScore;
        row.ConfidenceLevel = resale.ConfidenceLevel;
        // Sold evidence is the strongest kind there is: one real sale of this item is already
        // better proof of price than a dozen listings nobody has bought.
        row.Rankable = row.SampleCount >= 1;

        ApplyMoney(row, price, shipping, shipping > 0 ? shipping : null, fees, cost);
        ApplySpeed(row, resale);
        row.Note = $"{Money(row.NetProceeds!.Value)} of a {Money(row.GrossRevenue!.Value)} sale reaches you; "
                 + $"{Money(row.TotalDeductions!.Value)} goes to fees and getting it there.";
        return row;
    }

    // ── Everywhere else: priced off live local supply, or not priced at all ──────────────────
    private VenueOutlook BuildOffEbay(VenueSpec spec, LocalVenueEvidence? evidence, FeeProfile fees, decimal cost)
    {
        var row = NewRow(spec);
        row.Status = evidence?.Status ?? "no_data";
        row.Error = evidence?.Error;
        row.SearchUrl = evidence?.SearchUrl ?? "";
        row.RawResultCount = evidence?.RawResultCount ?? 0;

        var prices = (evidence?.Prices ?? []).Where(p => p > 0m).OrderBy(p => p).ToList();
        if (prices.Count == 0)
        {
            row.EvidenceKind = "none";
            (row.Verdict, row.EvidenceLabel, row.Note) = NoPriceReason(spec, evidence);
            row.PriceBasis = row.Note;
            return row;
        }

        var median = Math.Round(MarketplacePricingCalculator.Median(prices), 2);
        var realized = Math.Round(median * LocalAskRealization, 2);

        row.SampleCount = prices.Count;
        row.EvidenceKind = "asking";
        row.EvidenceLabel = $"{prices.Count} listing{(prices.Count == 1 ? "" : "s")} asking";
        row.ObservedMedian = median;
        row.ObservedLow = prices[0];
        row.ObservedHigh = prices[^1];
        row.RealizationFactor = LocalAskRealization;
        row.PriceBasis =
            $"{prices.Count} live listing{(prices.Count == 1 ? "" : "s")} nearby, median ask {Money(median)} "
            + $"(range {Money(prices[0])}–{Money(prices[^1])}). These are asks, not sales, so the estimate is "
            + $"{(1 - LocalAskRealization) * 100m:0.#}% under the median ask.";

        (row.Confidence, row.ConfidenceLevel) = ScoreAskingEvidence(prices);
        row.Rankable = prices.Count >= MinLocalSamples;

        // Local pickup: the buyer pays the sticker price and nothing else, so gross is the price.
        ApplyMoney(row, realized, buyerPaidShipping: 0m, shippingOverride: spec.SellerShips ? null : 0m, fees, cost);
        ApplySpeed(row, resale: null);

        row.Note = spec.SellerShips
            ? $"{Money(row.NetProceeds!.Value)} of a {Money(realized)} sale reaches you."
            : $"{Money(row.NetProceeds!.Value)} in cash — the whole price, no fee and no label.";
        if (!row.Rankable)
            row.Verdict = "thin";
        return row;
    }

    private static VenueOutlook NewRow(VenueSpec spec) => new()
    {
        Venue = spec.Key, VenueLabel = spec.Label,
        SaleMode = spec.SaleMode, SaleModeLabel = spec.SaleModeLabel,
        CreateUrl = spec.CreateUrl, FeeNote = spec.FeeNote,
        CashPipelineDays = spec.CashPipelineDays,
        ConfidenceLevel = "Insufficient Evidence",
    };

    // Why a venue has no price, in the seller's terms. "Nothing found" and "you never connected
    // this account" look identical in an empty column and mean opposite things.
    private static (string Verdict, string EvidenceLabel, string Note) NoPriceReason(
        VenueSpec spec, LocalVenueEvidence? evidence) => (evidence?.Status ?? "no_data") switch
    {
        "not_connected" => ("unavailable", "Not connected",
            $"{spec.Label} needs a saved login before it can be searched — connect it in Settings and run this again."),
        "session_expired" => ("unavailable", "Session expired",
            $"Your saved {spec.Label} session has expired, so its prices are missing here. Reconnect it in Settings."),
        "error" => ("unavailable", "Search failed",
            $"{spec.Label} could not be searched: {evidence?.Error ?? "the search failed"}."),
        _ when spec.Key == Mercari => ("no_data", "No price data",
            "This app cannot read Mercari prices, so there is no estimate of what it sells for there — "
            + "only the price it would have to fetch to beat eBay, which is fee arithmetic and is shown below."),
        _ => ("no_data", "Nothing matched",
            evidence is { RawResultCount: > 0 }
                ? $"{evidence.RawResultCount} nearby listing{(evidence.RawResultCount == 1 ? " was" : "s were")} found, "
                  + "but none of them matched this item closely enough to price it."
                : $"No matching {spec.Label} listings nearby, so there is no evidence of what it fetches there."),
    };

    // ── Money, via the one calculator ────────────────────────────────────────────────────────
    private void ApplyMoney(
        VenueOutlook row, decimal price, decimal buyerPaidShipping, decimal? shippingOverride,
        FeeProfile fees, decimal cost)
    {
        var breakdown = profitCalc.Calculate(
            supplierUnitCost: cost, quantity: 1, expectedSalePrice: price, quickSalePrice: price,
            buyerPaidShipping: buyerPaidShipping, fees: fees, actualShippingCostOverride: shippingOverride);

        var gross = Math.Round(price + buyerPaidShipping, 2);
        var deductions = Math.Round(breakdown.MarketplaceFeeTotal + breakdown.FulfilmentCostTotal, 2);

        row.ExpectedPrice = price;
        row.BuyerPaidShipping = buyerPaidShipping;
        row.GrossRevenue = gross;
        row.Fees = breakdown.MarketplaceFeeTotal;
        row.FulfilmentCost = breakdown.FulfilmentCostTotal;
        row.TotalDeductions = deductions;
        row.NetProceeds = Math.Round(gross - deductions, 2);
        row.FeeLoadPercent = gross > 0m ? Math.Round(deductions / gross * 100m, 1) : 0m;

        if (cost > 0m)
        {
            row.NetProfit = breakdown.NetProfitPerUnit;
            row.MarginPercent = breakdown.MarginPercent;
            row.RoiPercent = breakdown.RoiPercent;
        }

        row.Costs =
        [
            new("fee", $"{row.VenueLabel} selling fee", breakdown.EbayFees,
                row.Venue == Ebay ? "Final value fee on the sale plus the per-order fee" : "That site's cut of the sale"),
            new("promoted", "Promoted Listings", breakdown.PromotedListingFees, "Ad rate on the total sale"),
            new("shipping", "Shipping label", breakdown.ActualShippingCost,
                row.SaleMode == LocalPickupMode ? "Nothing to ship — the buyer collects it" : "What it costs you to ship it"),
            new("packaging", "Packaging", breakdown.PackagingCost,
                row.SaleMode == LocalPickupMode ? "No box, filler or tape needed" : "Box, filler, tape and label"),
            new("labor", "Handling", breakdown.LaborCost, "Your time on the sale"),
            new("returns", "Returns reserve", breakdown.ReturnReserve,
                row.SaleMode == LocalPickupMode ? "A cash handoff is final — nothing set aside" : "Set aside against the share that come back"),
            new("testing", "Testing reserve", breakdown.TestingReserve, "Set aside against units that fail testing"),
        ];
    }

    // How long the money takes to arrive. Only eBay has dated sold history behind it, so only eBay
    // gets a days-to-cash figure; a local venue reports the half it genuinely knows — that a cash
    // handoff skips the ship-and-payout wait entirely — and says the rest is unmeasured rather than
    // claiming a speed that would win it the comparison for free.
    private static void ApplySpeed(VenueOutlook row, ResalePricing? resale)
    {
        if (resale is not null)
        {
            // Profit once the seller has said what they paid, proceeds until then — the same figure
            // the row's headline number is, so "$3.40 a day" always refers to the money above it.
            var estimate = DaysToCashEstimator.Estimate(
                resale.EstimatedDaysToSell, resale.EstimatedMonthlySales,
                row.NetProfit ?? row.NetProceeds, row.RoiPercent);
            row.DaysToSell = estimate.DaysToSell;
            row.DaysToCash = estimate.DaysToCash;
            row.NetPerDay = estimate.ProfitPerDay;
            row.SpeedTier = estimate.SpeedTier;
            row.SpeedLabel = estimate.SpeedLabel;
            row.SpeedNote = estimate.Note;
            return;
        }

        row.SpeedTier = "unknown";
        row.SpeedLabel = row.CashPipelineDays == 0 ? "Cash at handoff" : "Speed unknown";
        row.SpeedNote = row.CashPipelineDays == 0
            ? "The money is in your hand when the buyer collects it — no delivery confirmation and no payout wait. "
            + "How long it takes to find that buyer locally is not something this data can measure."
            : "No sold history for this venue, so there is no honest estimate of how long it takes to sell here.";
    }

    // ── The price that matches eBay's take-home ──────────────────────────────────────────────
    // Net proceeds are net profit with a zero cost basis, so the break-even the calculator already
    // solves for is the price at which this venue hands over nothing — and NetProceedsCalculator's
    // existing floor identity turns that into "the price that hands over exactly what eBay does".
    // Reused rather than re-derived so the two can never drift apart.
    private void ApplyPriceToBeatEbay(VenueOutlook row, decimal? ebayNet, FeeProfile venueFees)
    {
        if (ebayNet is not decimal target || target <= 0m) return;

        var zeroCost = profitCalc.Calculate(
            supplierUnitCost: 0m, quantity: 1, expectedSalePrice: 0m, quickSalePrice: 0m,
            buyerPaidShipping: 0m, fees: venueFees,
            actualShippingCostOverride: row.SaleMode == LocalPickupMode ? 0m : null);

        var price = NetProceedsCalculator.ProfitFloorPrice(zeroCost.BreakEvenSalePrice, target, venueFees);
        if (price is not decimal parity) return;

        row.PriceToBeatEbay = parity;
        row.PriceToBeatEbayNote =
            $"Get {Money(parity)} here and you match eBay's {Money(target)} take-home. Anything above it is money eBay was keeping.";
    }

    // ── Ranking and the verdict ──────────────────────────────────────────────────────────────
    // Best money first among venues whose evidence earns a ranking; everything else keeps its place
    // below them, in the order the venues are declared. A venue is never dropped — "we could not
    // price Mercari" is information, and a blank row is not.
    private static void Rank(List<VenueOutlook> rows, decimal? ebayNet)
    {
        var ordered = rows
            .OrderByDescending(r => r.Rankable && r.NetProceeds is not null)
            .ThenByDescending(r => r.NetProceeds ?? decimal.MinValue)
            .ToList();

        rows.Clear();
        rows.AddRange(ordered);

        var best = rows.FirstOrDefault(r => r.Rankable && r.NetProceeds is not null);
        var rank = 0;

        foreach (var row in rows)
        {
            // Every priced venue gets the gap, ranked or not — a thin row that looks like it pays
            // $40 more is exactly the row a seller wants to go and verify for themselves.
            if (ebayNet is decimal baseline && row.NetProceeds is decimal net && row.Venue != Ebay)
                row.NetVsEbay = Math.Round(net - baseline, 2);

            // Thin, unavailable and unpriced rows keep the verdict that explains why.
            if (!row.Rankable || row.NetProceeds is null) continue;

            row.Rank = ++rank;
            row.Verdict = ReferenceEquals(row, best) ? "best"
                : row.NetVsEbay is > 0m ? "close"
                : "lower";
        }

        // A win that isn't worth the move is not a win. The top row keeps its rank — it really does
        // pay a little more — but it loses the badge that would send a seller to another site for
        // two dollars.
        if (best is not null && best.Venue != Ebay && ebayNet is decimal ebayBaseline)
        {
            var gap = (best.NetProceeds ?? 0m) - ebayBaseline;
            if (gap <= 0m || !IsMaterial(gap, ebayBaseline)) best.Verdict = "close";
        }
    }

    /// <summary>
    /// Whether a gap is worth changing where you sell. Both bars must clear: a flat five dollars
    /// stops a rounding difference on a cheap item reading as a recommendation, and the percentage
    /// stops a $9 edge on a $600 item — well inside the error bars of an ask-based estimate — from
    /// doing the same.
    /// </summary>
    public static bool IsMaterial(decimal gap, decimal? ebayNet)
    {
        if (gap < MaterialGapDollars) return false;
        if (ebayNet is not decimal baseline || baseline <= 0m) return true;
        return gap >= baseline * MaterialGapPercent / 100m;
    }

    private static (string Verdict, string Headline, string Subhead, decimal? Extra) Describe(
        VenueOutlook? winner, VenueOutlook ebayRow, decimal? ebayNet, bool hasCostBasis)
    {
        if (winner is null)
            return ("no_data", "Not enough evidence to say where this sells highest",
                "Neither eBay sold history nor nearby listings priced this item. Try a shorter, more "
                + "specific search — a model number usually matches where a full listing title does not.", null);

        // A negative number is not "profit", and the sentence has to say so — the venue that loses
        // you the least is still a venue that loses you money, and that is the fact worth leading
        // with once the seller has told us what they paid.
        var profitSuffix = (hasCostBasis, winner.NetProfit) switch
        {
            (true, > 0m) => $" That is {Money(winner.NetProfit!.Value)} of profit on what you paid.",
            (true, not null) => $" Even so, what you paid means the best venue here still loses "
                              + $"{Money(Math.Abs(winner.NetProfit!.Value))} on this item.",
            _ => " Add what you paid for it to turn these into profit figures.",
        };

        if (winner.Venue == Ebay)
        {
            var challenger = ebayNet is decimal baseline ? $"{Money(baseline)}" : "the most";
            return ("stay_on_ebay", $"eBay still pays best here — {challenger} in your pocket",
                $"{ebayRow.PriceBasis} Nothing nearby nets more after fees, so list it where you already are."
                + profitSuffix, 0m);
        }

        // The winner is off eBay but eBay itself could not be priced. There is a number to report
        // and no comparison to make, and saying otherwise would be inventing the baseline.
        if (ebayNet is null)
            return ("move", $"{Money(winner.NetProceeds!.Value)} on {winner.VenueLabel}",
                $"{winner.PriceBasis} {ebayRow.Note} So this is the only venue with evidence behind it, "
                + "not a venue proven to beat eBay." + profitSuffix, null);

        var extra = winner.NetVsEbay ?? 0m;
        if (extra <= 0m || !IsMaterial(extra, ebayNet))
        {
            var money = winner.NetProceeds is decimal net ? Money(net) : "about the same";
            return ("too_close", $"{winner.VenueLabel} and eBay pay about the same — {money} either way",
                "The difference is inside the error bars of an asking-price estimate, so it is not a "
                + "reason to move. Stay on eBay unless you would rather not ship it." + profitSuffix,
                Math.Max(0m, extra));
        }

        var speed = winner.SaleMode == LocalPickupMode
            ? " — cash on handoff, no box, no label and no payout wait"
            : "";

        return ("move", $"Sell it on {winner.VenueLabel}: {Money(winner.NetProceeds!.Value)} vs {Money(ebayNet!.Value)} on eBay",
            $"That is {Money(extra)} more on this one item{speed}. {winner.PriceBasis}{profitSuffix}", extra);
    }

    // Confidence in an asking-price estimate: how many listings there are, and how much they agree.
    // Capped below the "Good Confidence" band on purpose — asks are not sales, and no quantity of
    // them earns the same standing as sold history. Same level vocabulary as
    // ConfidenceScoringService so the two read as one scale in the UI.
    public static (int Score, string Level) ScoreAskingEvidence(IReadOnlyList<decimal> sortedPrices)
    {
        if (sortedPrices.Count == 0) return (0, "Insufficient Evidence");

        var countScore = Math.Min(sortedPrices.Count, 8) / 8.0 * 45.0;

        var median = (double)MarketplacePricingCalculator.Median(sortedPrices);
        var spread = median > 0 ? (double)(sortedPrices[^1] - sortedPrices[0]) / median : 1.0;
        var consistencyScore = Math.Clamp(1.0 - spread / 2.0, 0.0, 1.0) * 20.0;

        var score = (int)Math.Round(Math.Clamp(countScore + consistencyScore, 0, 64));
        return (score, score >= 40 ? "Limited Confidence" : "Insufficient Evidence");
    }

    private static void AddWarnings(WhereToSellReport report, List<VenueOutlook> rows, ResalePricing? ebay)
    {
        if (rows.All(r => r.NetProceeds is null))
            report.Warnings.Add("Nothing could be priced on any venue, so there is no comparison to make.");

        if (ebay?.DisagreementMessage is string disagreement)
            report.Warnings.Add(disagreement);

        var unavailable = rows.Where(r => r.Verdict == "unavailable").Select(r => r.VenueLabel).ToList();
        if (unavailable.Count > 0)
            report.Warnings.Add($"Not searched: {string.Join(", ", unavailable)}. "
                + "A venue that could not be searched is missing from this comparison, not losing it.");

        if (rows.Any(r => r.EvidenceKind == "asking" && r.Rankable))
            report.Warnings.Add("Off-eBay figures come from what nearby sellers are ASKING right now — "
                + "there is no public sold history for those sites. eBay's figure comes from what buyers paid.");
    }

    private static string Money(decimal value) =>
        value.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
}
