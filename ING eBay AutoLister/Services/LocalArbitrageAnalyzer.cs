using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

// The resale half of one arbitrage row, flattened out of whatever priced it — the hosted
// sold-comps database, Terapeak, or a blend of the two (MarketPriceEstimator decides the
// weighting; this just carries the answer). Kept as its own small type so the profit math
// below is testable without constructing a whole MarketAnalysisResult.
public sealed class ResalePricing
{
    public string LookupTitle { get; set; } = "";
    public decimal? Median { get; set; }
    public decimal? ExpectedSale { get; set; }
    public decimal? QuickSale { get; set; }
    public int SoldCompCount { get; set; }
    public int TerapeakCompCount { get; set; }
    public decimal SoldCompWeightPercent { get; set; }
    public decimal TerapeakWeightPercent { get; set; }
    // How many of those sold comps actually produced the price — after the identity guard, outlier
    // removal and the strong-match filter. SoldCompCount is what the lookup returned, which is the
    // flattering number: a search can return twelve rows and price the item off one of them.
    public int PricedCompCount { get; set; }
    // False when this item states a model/part number and no comp title carries it. The price is
    // then a price for something else that shares a brand — see MarketPriceEstimator.GuardIdentity.
    public bool IdentityVerified { get; set; } = true;
    // The evidence every verdict and every percentage is judged against. Falls back to the raw
    // count only for the hand-built ResalePricing objects (the trend projection, the negotiation
    // endpoint) that never ran a lookup and state their comp count directly — the real pricing path
    // always sets PricedCompCount, including to zero.
    public int EvidenceCompCount =>
        (PricedCompCount > 0 ? PricedCompCount : SoldCompCount) + TerapeakCompCount;
    // What buyers paid for shipping on the matched comps. Booked as revenue AND as cost
    // (see LocalArbitrageAnalyzer.Build) rather than as either one alone.
    public decimal AvgCompShipping { get; set; }
    public int ConfidenceScore { get; set; }
    public string ConfidenceLevel { get; set; } = "Insufficient Evidence";
    public string? DisagreementMessage { get; set; }
    public int LiquidityScore { get; set; }
    public string LiquidityLevel { get; set; } = "";
    // How long the money stays tied up, and how many of these move a month. Every board that ranks
    // opportunities runs these through DaysToCashEstimator, because capital parked in a slow mover
    // is money that can't buy the next flip — see LocalArbitrageAnalyzer.Build.
    public int? EstimatedDaysToSell { get; set; }
    public decimal EstimatedMonthlySales { get; set; }
    public int OpportunityScore { get; set; }

    public bool HasPrice => ExpectedSale is > 0 || Median is > 0;

    public static ResalePricing From(MarketAnalysisResult analysis, string lookupTitle)
    {
        var comps = analysis.TopSoldComparables;
        return new ResalePricing
        {
            LookupTitle = lookupTitle,
            Median = analysis.PriceEstimate.MedianPrice,
            ExpectedSale = analysis.PriceEstimate.ExpectedSalePrice,
            QuickSale = analysis.PriceEstimate.QuickSalePrice,
            SoldCompCount = analysis.Sources.LocalComparableCount,
            TerapeakCompCount = analysis.Sources.TerapeakComparableCount,
            PricedCompCount = analysis.Sources.PricedOnCompCount,
            IdentityVerified = analysis.Sources.IdentityVerified,
            SoldCompWeightPercent = Math.Round(analysis.Sources.LocalWeightPercent, 0),
            TerapeakWeightPercent = Math.Round(analysis.Sources.TerapeakWeightPercent, 0),
            AvgCompShipping = comps.Count > 0 ? Math.Round(comps.Average(c => c.Shipping), 2) : 0m,
            ConfidenceScore = analysis.Confidence.Score,
            ConfidenceLevel = analysis.Confidence.Level,
            DisagreementMessage = analysis.PriceEstimate.DisagreementMessage,
            LiquidityScore = analysis.SellThrough.LiquidityScore,
            LiquidityLevel = analysis.SellThrough.LiquidityLevel,
            EstimatedDaysToSell = analysis.SellThrough.EstimatedDaysToSell,
            EstimatedMonthlySales = analysis.SellThrough.EstimatedMonthlySales,
            OpportunityScore = analysis.Score.Score,
        };
    }
}

// Several local listings for the same product share one comp lookup: the resale side is a
// property of the product, not of who is selling it locally, and pricing five listings of the
// same drill five times would spend five times the lookups for one answer.
//
// Grouping runs across sources, not within one, which is where multi-source pays for itself: the
// same drill on Craigslist and on Facebook is one comp lookup, not two.
public sealed class LocalArbitrageGroup
{
    public string Key { get; set; } = "";
    // The fullest title in the group — local titles for the same item range from
    // "Antminer S19j Pro 104TH miner" to "miner", and the comp matcher can only work with
    // what it's given.
    public string LookupTitle { get; set; } = "";
    public List<LocalSupplyListing> Listings { get; set; } = [];

    public decimal LowestAsk => Listings.Where(l => l.Price is > 0).Select(l => l.Price!.Value)
        .DefaultIfEmpty(0m).Min();
}

/// <summary>
/// Ranks local Marketplace supply by what it's actually worth flipping: net profit after eBay
/// fees, ROI and margin against real sold data, not the gross spread the per-card check shows.
///
/// Everything here is pure except <see cref="Build"/>, which delegates the money to the shared
/// <see cref="ProfitCalculator"/>/<see cref="FeeProfile"/> so a local flip is costed by exactly
/// the same rules as a dropship or supplier-file item.
/// </summary>
public sealed class LocalArbitrageAnalyzer(
    ProfitCalculator profitCalc, LiquidationLotPricer liquidationPricer,
    ResaleValuationRegistry? valuations = null)
{
    // What is allowed to put a resale price on a row, per category. Defaulted rather than required
    // so every caller that constructs this by hand — and every test that does — is unchanged, while
    // the container still injects the registered providers. See Services/ResaleValuation.cs.
    private readonly ResaleValuationRegistry _valuations = valuations ?? ResaleValuationRegistry.Default;

    // A "goldmine" has to be earned on both axes — a big multiple AND enough sold history to
    // believe it. Thin data gets the honest label instead of the green badge.
    //
    // Public because Roll the Dice quotes the same bar back at the seller ("pay under $X and this
    // clears the goldmine threshold") — see JackpotHunter.TargetBuyPrice. One definition of a
    // goldmine, not a second, friendlier one for the flashier feature.
    public const decimal GoldmineRoiPercent = 75m;
    public const decimal GoldmineProfit = 75m;
    public const int GoldmineMinComps = 5;
    public const int GoldmineMinConfidence = 50;
    // The "worth the drive" bar. Public for the same reason the goldmine bar is: NegotiationAdvisor
    // quotes it back as the ceiling to stop bidding at, and a negotiation that walked at a different
    // number than the board judges by would be two definitions of "worth doing".
    public const decimal SolidRoiPercent = 30m;
    public const decimal SolidProfit = 25m;
    // Below this the sold history is too sparse to call anything, however good the arithmetic.
    // Counted in comps that ACTUALLY PRICED the item, never in comps the lookup returned — see
    // ResalePricing.EvidenceCompCount. Three is the floor at which a median means anything at all:
    // two sales cannot disagree with each other, so two sales cannot be checked.
    public const int ThinCompCount = 3;

    // ── Evidence tiers ───────────────────────────────────────────────────────────────────────
    // What the money columns are allowed to claim. Public because the UI dims on them and the
    // tests assert on them, and because "confident" has to mean one thing across the app.
    // Spelled once, in Models, so a ResaleValuation can name a tier without depending on this class.
    public const string EvidenceConfident = LocalArbitrageEvidence.Confident;
    public const string EvidenceLow = LocalArbitrageEvidence.Low;
    public const string EvidenceNone = LocalArbitrageEvidence.None;

    /// <summary>
    /// How much to believe this row's resale price, and therefore its ROI and margin.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The failure this exists to stop: a $60 trailer hitch matched to one loose sold comp at $554,
    /// published as a 698% return in the same typeface as a return backed by twenty sales. The
    /// arithmetic was never wrong — <see cref="ProfitCalculator"/> did exactly what it was asked —
    /// but the price it was handed rested on nothing, and nothing on the row said so.
    /// </para>
    /// <para>
    /// Two things can sink a row here and they are different failures. Too FEW comps means the
    /// price might be right and cannot be checked. An unverified identity means the price is for
    /// something else — twenty comps of the wrong product is worse evidence than two of the right
    /// one, so the count never rescues it.
    /// </para>
    /// <para>Pure and total: takes the counts, returns the tier and the sentence that explains it.</para>
    /// </remarks>
    public static (string Tier, string Note) GradeEvidence(
        int pricedCompCount, int terapeakCompCount, bool identityVerified, int confidenceScore)
    {
        var comps = Math.Max(0, pricedCompCount) + Math.Max(0, terapeakCompCount);

        if (comps == 0)
            return (EvidenceNone, "No sold comps matched this item — there is no resale price to check the ask against.");

        // Priced off the wrong product. Said first and said plainly: no number of comps for a
        // different item adds up to evidence about this one.
        if (!identityVerified)
        {
            return (EvidenceLow,
                $"Estimate only — not one of the {comps} sold comp{(comps == 1 ? "" : "s")} carries this item's " +
                "model or part number, so the resale price behind these percentages is another product's.");
        }

        if (comps < ThinCompCount)
        {
            return (EvidenceLow,
                $"Estimate — priced off {comps} sold comp{(comps == 1 ? "" : "s")}, too few to call a market price. " +
                $"One unusual sale sets the whole figure until there are {ThinCompCount}.");
        }

        // Enough comps, and they are this item's — but the scorer still found the history weak
        // (stale, scattered, or inconsistent in condition). Believable as a guide, not as a rate.
        if (confidenceScore < GoldmineMinConfidence)
        {
            return (EvidenceLow,
                $"Estimate — {comps} sold comps, but their prices and dates are too scattered to trust the exact figure.");
        }

        return (EvidenceConfident,
            $"Backed by {comps} sold comp{(comps == 1 ? "" : "s")} that match this item.");
    }

    // Only a confident row may wear a green badge or state a percentage as a rate. Everything else
    // is capped at "thin" — the arithmetic still shows, labelled as the estimate it is.
    private static void ApplyEvidence(LocalArbitrageOpportunity row, ResalePricing resale)
    {
        var (tier, note) = GradeEvidence(
            resale.PricedCompCount > 0 ? resale.PricedCompCount : resale.SoldCompCount,
            resale.TerapeakCompCount, resale.IdentityVerified, resale.ConfidenceScore);

        row.EvidenceTier = tier;
        row.EvidenceNote = note;
        // One grading, read two ways: the row dims its percentages on this, and the valuation chip
        // beside the resale price states the same word. Two independent gradings of the same
        // evidence is how a board ends up calling one number confident in two places and thin in a
        // third.
        if (row.Valuation is { } valuation) valuation.Confidence = tier;
    }

    /// <summary>
    /// Prices one listing. <paramref name="retailSalesTaxPercent"/> applies only to rows from a
    /// retail source (the deal feeds) — a private-party buy is cash, and defaulting the parameter
    /// keeps every caller that only ever sees local supply unchanged.
    /// </summary>
    /// <param name="coupons">
    /// Public promo codes and cashback offers published for this row's store, or null. Defaulted so
    /// every caller that doesn't source from a till is unchanged. What they produce is a second set
    /// of money figures beside the row's own — never a change to them; see <see cref="ApplyCoupons"/>.
    /// </param>
    public LocalArbitrageOpportunity Build(
        LocalSupplyListing listing, ResalePricing? resale, FeeProfile fees,
        decimal retailSalesTaxPercent = RetailBuyCosts.DefaultSalesTaxPercent,
        IReadOnlyList<CouponOffer>? coupons = null)
    {
        var localAsk = listing.Price ?? 0m;

        // What KIND of thing this is, decided upstream by the scan and merely read here — see
        // ResaleCategoryCatalog. Everything below reads it twice: once to decide what may value it,
        // and once to decide what selling it costs. An unclassified listing resolves to "anything",
        // which is the parcel model this board has always used.
        var category = ResaleCategoryCatalog.Resolve(listing.CategoryId);

        // The gate that stops this board publishing a made-up number. The comps lookup upstream will
        // answer for anything it is asked about, including a truck it has only ever seen tow hitches
        // for; the provider for this category decides whether that answer is a price for THIS kind
        // of thing, and hands back nothing at all when it isn't.
        var valued = _valuations.Value(category, listing, resale);
        resale = valued.Pricing;

        // What the listing says about cover. A source that already knows (a refurb outlet publishing
        // its own programme terms) wins; everything else is read here rather than in each parser, so
        // every board that reaches this method gets warranty at once and no parser has to learn the
        // word. See WarrantyDetector, which is pure and answers null for most rows.
        var warranty = listing.Warranty
            ?? WarrantyDetector.Detect(listing.Title, listing.DetailText, listing.Retailer, DateTime.UtcNow);

        // A free item's cost basis is not its price — it is what surviving the word "free" costs:
        // a rebate's unrefundable sales tax, the slice of the claim held in reserve, and nothing at
        // all on a curb pickup. Computed first because everything below is measured against it.
        var freebie = listing.Freebie is { } details
            ? FreebiePricer.Cost(details, retailSalesTaxPercent)
            : null;

        // The register's cut. Zero for every non-retail row, which is what makes this change
        // invisible to Craigslist and Facebook: taxPercent 0 ⇒ buyCost == localAsk exactly.
        var taxPercent = listing.IsRetail ? RetailBuyCosts.Sanitize(retailSalesTaxPercent) : 0m;
        var salesTax = RetailBuyCosts.TaxOn(localAsk, taxPercent);
        var buyCost = localAsk + salesTax;

        // On a freebie the pricer has already done both, and it is the only one that can: a rebate's
        // tax is charged against a price the seller gets back, which this row's LocalAsk (zero)
        // knows nothing about.
        if (freebie is not null)
        {
            salesTax = freebie.SalesTax;
            buyCost = freebie.NetCost;
        }

        var row = new LocalArbitrageOpportunity
        {
            Source = listing.Source,
            SourceLabel = listing.SourceLabel,
            ItemId = listing.ItemId,
            Title = listing.Title,
            Url = listing.Url,
            ImageUrl = listing.ImageUrl,
            LocalAsk = localAsk,
            OriginalPrice = listing.OriginalPrice,
            Location = listing.Location,
            DistanceMiles = listing.DistanceMiles,
            PostedAgo = listing.PostedAgo,
            PostedUtc = listing.PostedUtc,
            IsRetail = listing.IsRetail,
            Retailer = listing.Retailer,
            FreeShipping = listing.FreeShipping,
            CouponCode = listing.CouponCode,
            CategoryId = category.Id,
            CategoryLabel = category.Label,
            Vehicle = listing.Vehicle,
            Valuation = valued.Valuation,
            Freebie = freebie,
            // Only stated where it is a real, separate cost — carrying a $0 tax line on a
            // Craigslist row would imply the app had checked, and it hasn't. A freebie states both
            // whenever there is anything to state: on a rebate the seller is genuinely out of pocket
            // for weeks, and a board showing that row at $0 would be describing a different deal.
            SalesTax = freebie is not null ? (freebie.SalesTax > 0m ? freebie.SalesTax : null)
                : listing.IsRetail ? salesTax : null,
            BuyCostAllIn = freebie is not null ? (buyCost > 0m ? buyCost : null)
                : listing.IsRetail ? buyCost : null,
        };

        // How this category is costed: which fees apply, whether anything ships, and what a title
        // and a tow add. On the default category this hands back the seller's own profile and the
        // comp shipping, unchanged — every parcel row prices exactly as it did before categories
        // existed. See CategoryCosts.
        var quote = CategoryCosts.For(category, fees, resale?.AvgCompShipping ?? 0m);
        row.Category = quote.Economics;

        // An auction lot is a different arithmetic on the same pipeline: the price is a bid rather
        // than an ask, a buyer's premium sits on top of it, and the row may be several units of one
        // product. It gets its own branch rather than a pile of conditionals through this one — see
        // LiquidationLotPricer, which does the money through the Liquidation Lot Analyzer's own
        // grade, cost and max-bid arithmetic.
        //
        // Costed on the seller's own profile rather than the category's: a pallet is a pallet of
        // parcels whatever is in it, and the lot pricer already has its own premium, tax and
        // per-unit arithmetic that a vehicle fee model would have nothing to say to.
        if (listing.Liquidation is { } lot)
            return BuildLiquidation(row, lot, warranty, resale, fees, retailSalesTaxPercent);

        if (resale is null || !resale.HasPrice)
        {
            row.Verdict = "no_data";
            // The reason, from whatever refused to price it. "No sold history matched this title" and
            // "the comps for this truck are tow hitches" are different problems with different next
            // steps, and the row is the only place the seller finds out which one they have.
            row.VerdictNote = valued.Valuation.Note.Length > 0
                ? valued.Valuation.Note
                : "No eBay sold history matched this title.";
            row.PricedAs = resale?.LookupTitle ?? "";
            if (resale is not null) ApplyEvidence(row, resale);
            // No price to add a premium to, and still worth saying out loud: "this one is under
            // factory warranty until March" is the most useful thing an unpriceable row can carry,
            // and it is the reason to go and look at it by hand.
            row.Warranty = warranty is null ? null
                : WarrantyPricer.Value(warranty, 0m, buyCost, 0, 0, allowUplift: false);
            ApplyDaysToCash(row, resale);
            // A row nobody could price still buys cheaper with a code. There is no profit to
            // recompute, so what the seller gets is the discount and the code itself — which is
            // more than the dash in every money column beside it.
            ApplyCoupons(row, coupons, resale, quote, taxPercent, baseProfit: null);
            return row;
        }

        ApplyResale(row, resale);

        var expected = resale.ExpectedSale is > 0 ? resale.ExpectedSale!.Value : resale.Median!.Value;

        // Remaining cover, priced. This is the only place in the app where a listing's own prose can
        // lift a resale estimate above the sold comps, and WarrantyPricer refuses far more often than
        // it allows: the cover has to be STATED, transferable, still running, and sitting on comps
        // solid enough to add a premium to — then capped as a percentage and again in dollars.
        //
        // When it does allow one, the row's own resale column moves with it. A profit computed
        // against a price the row doesn't show is a row that doesn't add up, and every figure below
        // — fees, net, ROI, max buy price — is meant to be checkable against the column beside it.
        // Counted in comps that priced it, not comps that were found — so a warranty premium and a
        // verdict both rest on the same evidence the resale price does.
        var compCount = resale.EvidenceCompCount;
        if (warranty is not null)
        {
            row.Warranty = WarrantyPricer.Value(warranty, expected, buyCost, compCount, resale.ConfidenceScore);
            if (row.Warranty.ResaleUplift > 0m)
            {
                expected = row.Warranty.ResaleWithWarranty;
                row.EbayExpectedSale = expected;
            }
        }

        // Shipping is booked on both sides: buyers paid it (revenue, and eBay charges its final
        // value fee on it) and it costs the seller the same amount to actually ship. Booking it
        // on one side only is how a profit estimate ends up either inflated or double-charged.
        // When the comps sold with free shipping there is no observed figure, so this falls back
        // to FeeProfile.DefaultShippingCost like every other profit path in the app.
        // The cost basis is what actually leaves the wallet, which on a retail row includes the
        // sales tax. ROI comes back measured against that same figure — money spent is money spent,
        // whether it went to the retailer or to the state.
        //
        // Which fee profile, which shipping and which extra costs is the category's answer, not
        // this method's: a truck pays a flat eBay Motors fee and no postage at all, a fridge pays
        // eBay's percentage and is collected, and everything else pays exactly what it always did.
        // One calculator either way — the category only decides what it is handed.
        var profit = profitCalc.Calculate(
            supplierUnitCost: buyCost, quantity: 1, expectedSalePrice: expected,
            quickSalePrice: resale.QuickSale ?? expected,
            buyerPaidShipping: quote.BuyerPaidShipping, fees: quote.Fees,
            actualShippingCostOverride: quote.ShippingOverride,
            otherCosts: quote.OtherCosts);

        // The marketplace's cut, and only that. A title transfer is a real cost and it is not a fee
        // eBay charges — folding it into this column would misattribute it and make the row's own
        // fee basis unverifiable against the number beside it. It shows on its own line instead.
        row.EstimatedFees = Math.Round(profit.MarketplaceFeeTotal - profit.OtherCosts, 2);
        row.EstimatedShipCost = profit.FulfilmentCostTotal;
        row.NetProfit = profit.NetProfitPerUnit;
        row.RoiPercent = profit.RoiPercent;
        row.MarginPercent = profit.MarginPercent;
        // The highest ASKING price that still breaks even — the number a seller compares against
        // the price on the shelf. Without tax that is simply the ask plus the profit, because net
        // profit falls a dollar per dollar paid; with tax it falls (1 + rate) per dollar, so the
        // headroom is smaller. Quoting the untaxed figure would name a price that loses money.
        row.MaxBuyPrice = RetailBuyCosts.BreakEvenSticker(localAsk, profit.NetProfitPerUnit, taxPercent);

        // How long that profit takes to become money again. Computed from THIS row's net profit, so
        // two listings of the same product at different asks correctly differ in profit-per-day
        // even though they share a velocity. A rebate adds its own wait: the cash is out of the
        // seller's hands from the till to the cheque, exactly the way inventory is.
        ApplyDaysToCash(row, resale, freebie?.RefundWaitDays ?? 0);

        // Judged on the all-in cost, so a verdict never rests on a price the seller doesn't pay —
        // and on the comps that priced it, so it never rests on a resale price nothing backs.
        var (verdict, note) = Judge(profit.NetProfitPerUnit, profit.RoiPercent, buyCost,
            compCount, resale.ConfidenceScore, resale.IdentityVerified);

        if (freebie is not null) (verdict, note) = JudgeFreebie(verdict, note, freebie, profit.NetProfitPerUnit);

        // Cover the listing states is absent, on a buy big enough for that to BE the loss. Lowers a
        // verdict and never raises one — the uplift above has already had its say through the money,
        // and this is the part the money cannot express.
        if (row.Warranty is { } cover) (verdict, note) = WarrantyPricer.JudgeWarranty(verdict, note, cover);

        row.Verdict = verdict;
        row.VerdictNote = note;

        // What to actually say to the person selling it. Pure arithmetic on numbers already computed
        // above, so it costs nothing per row and can never disagree with the money columns beside it.
        ApplyNegotiation(row, resale);

        // The buy side of a retail row, which has no negotiation: the codes and cashback published
        // for the store, and the same flip re-costed at what they leave it costing.
        ApplyCoupons(row, coupons, resale, quote, taxPercent, profit);
        return row;
    }

    /// <summary>
    /// Cuts the buy price with whatever public codes exist for this store, and re-runs the profit
    /// against the discounted cost.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row's own numbers are left exactly as they were, and that is the whole design. A promo
    /// code off a public list is a <b>claim</b>: it may be dead, regional, category-limited or
    /// new-customers-only, and nothing short of checking out can test it. Recomputing
    /// <see cref="LocalArbitrageOpportunity.NetProfit"/> at the discounted cost would put that claim
    /// underneath the ranking, the verdict badge, the goldmine count and the board's profit total —
    /// so one dead code would quietly promote a deal that doesn't exist.
    /// </para>
    /// <para>
    /// What the seller gets instead is both numbers: what this makes at the shelf price, and what it
    /// makes if the code works, with the code, its conditions and its deadline printed beside them.
    /// The same posture <see cref="NegotiationAdvisor"/> takes with an opening offer nobody has
    /// accepted yet.
    /// </para>
    /// </remarks>
    private void ApplyCoupons(
        LocalArbitrageOpportunity row, IReadOnlyList<CouponOffer>? coupons, ResalePricing? resale,
        CategoryCosts.CategoryQuote quote, decimal taxPercent, ProfitBreakdown? baseProfit)
    {
        if (coupons is null || coupons.Count == 0) return;

        // Nobody selling a drill on Craigslist takes a promo code, an auctioneer takes bids rather
        // than codes, and there is nothing to discount on an item that is already free.
        if (!row.IsRetail || row.Liquidation is not null || row.Freebie is not null || row.LocalAsk <= 0) return;

        var stack = CouponStacker.Best(
            coupons, row.LocalAsk, taxPercent,
            // The advertised price already needs this code, so a second one cannot stack on it.
            existingCode: row.CouponCode, shipsFree: row.FreeShipping);

        // Nothing usable and nothing worth reading. Attaching an empty block would put a coupon
        // column on the row that says only "we looked".
        if (!stack.HasSaving && stack.AlsoFound.Count == 0) return;

        var savings = new CouponSavings
        {
            MerchantId = stack.Applied.Concat(stack.AlsoFound).Select(o => o.MerchantId).FirstOrDefault() ?? "",
            MerchantLabel = row.Retailer,
            Applied = stack.Applied,
            AlsoFound = stack.AlsoFound,
            Discount = stack.Discount,
            DiscountedSubtotal = stack.DiscountedSubtotal,
            SalesTax = stack.SalesTax,
            CashbackExpected = stack.CashbackExpected,
            CashbackReserve = stack.CashbackReserve,
            CashbackWaitDays = stack.CashbackWaitDays,
            BuyCostWithCoupons = stack.NetCost,
            Confidence = stack.Confidence,
            Note = stack.Note,
        };

        if (stack.HasSaving && baseProfit is not null && resale is not null && resale.HasPrice)
        {
            var expected = resale.ExpectedSale is > 0 ? resale.ExpectedSale!.Value : resale.Median!.Value;

            // Identical to the row's own call in every argument but the cost basis — anything else
            // would make the two figures incomparable, which is the only thing they are for.
            var discounted = profitCalc.Calculate(
                supplierUnitCost: stack.NetCost, quantity: 1, expectedSalePrice: expected,
                quickSalePrice: resale.QuickSale ?? expected,
                buyerPaidShipping: quote.BuyerPaidShipping, fees: quote.Fees,
                actualShippingCostOverride: quote.ShippingOverride,
                otherCosts: quote.OtherCosts);

            savings.NetProfitWithCoupons = discounted.NetProfitPerUnit;
            savings.RoiPercentWithCoupons = discounted.RoiPercent;
            savings.ExtraProfit = Math.Round(discounted.NetProfitPerUnit - baseProfit.NetProfitPerUnit, 2);
            savings.RescuesTheDeal = baseProfit.NetProfitPerUnit <= 0 && discounted.NetProfitPerUnit > 0;

            // The badge this row would wear if the code works, when that is better than the one it
            // has. Judged by the same bars as everything else on the board — a coupon does not get
            // its own, friendlier definition of a goldmine.
            var (verdict, _) = Judge(
                discounted.NetProfitPerUnit, discounted.RoiPercent, stack.NetCost,
                resale.EvidenceCompCount, resale.ConfidenceScore, resale.IdentityVerified);

            if (verdict != row.Verdict && VerdictRank(verdict) > VerdictRank(row.Verdict))
                savings.VerdictIfItWorks = verdict;
        }

        row.Coupons = savings;
    }

    // Only an improvement is worth stating. A code that leaves the verdict where it was has already
    // said everything it has to say in the money columns.
    private static int VerdictRank(string verdict) => verdict switch
    {
        "goldmine" => 3, "solid" => 2, "thin" => 1, _ => 0,
    };

    // The resale half of a row, which is a property of the product and identical however the buy
    // side is costed. Shared by the ordinary path and the liquidation one so the two can't drift.
    private static void ApplyResale(LocalArbitrageOpportunity row, ResalePricing resale)
    {
        row.PricedAs = resale.LookupTitle;
        row.EbayResaleMedian = resale.Median;
        row.EbayExpectedSale = resale.ExpectedSale;
        row.EbayQuickSale = resale.QuickSale;
        row.SoldCompCount = resale.SoldCompCount;
        row.TerapeakCompCount = resale.TerapeakCompCount;
        row.SoldCompWeightPercent = resale.SoldCompWeightPercent;
        row.TerapeakWeightPercent = resale.TerapeakWeightPercent;
        row.PricedCompCount = resale.PricedCompCount;
        row.IdentityVerified = resale.IdentityVerified;
        row.ResaleSource = SourceLabel(resale.SoldCompCount, resale.TerapeakCompCount);
        ApplyEvidence(row, resale);
        row.ConfidenceScore = resale.ConfidenceScore;
        row.ConfidenceLevel = resale.ConfidenceLevel;
        row.DisagreementMessage = resale.DisagreementMessage;
        row.LiquidityScore = resale.LiquidityScore;
        row.LiquidityLevel = resale.LiquidityLevel;
    }

    /// <summary>
    /// One auction lot, priced.
    ///
    /// Three things differ from the row above and all three are money. The price is a <b>bid</b>, so
    /// <see cref="LocalArbitrageOpportunity.MaxBuyPrice"/> becomes the highest bid worth making
    /// rather than the highest sticker worth paying. A <b>buyer's premium</b> and sales tax sit on
    /// top of it, so every figure is measured against the all-in cost. And the lot may be several
    /// <b>units</b>, so the profit is per unit times the units expected to survive the grade — which
    /// is the arithmetic <see cref="LotAnalyzer"/> has always done for a pasted manifest, called
    /// here rather than written again.
    /// </summary>
    private LocalArbitrageOpportunity BuildLiquidation(
        LocalArbitrageOpportunity row, LiquidationLotDetails lot, WarrantyDetails? warranty,
        ResalePricing? resale, FeeProfile fees, decimal salesTaxPercent)
    {
        var quote = liquidationPricer.Price(lot, row.LocalAsk, resale, fees, salesTaxPercent);

        row.Liquidation = quote.Economics;
        // Filled in on every liquidation row, priced or not: "this bid costs you $124 all in" is
        // true and worth knowing even when the resale side has no answer.
        row.SalesTax = quote.Economics.SalesTax;
        row.BuyCostAllIn = quote.TotalCost;
        row.PricedAs = resale?.LookupTitle ?? "";

        // Read and shown, never priced in. A lot's number is its grade times its unit count, and a
        // warranty line in the catalogue describes one unit of it — but "these are sealed and still
        // under factory cover" is exactly what separates a good pallet from a bad one, so the seller
        // gets to read it. The refusal says so in the row's own words rather than silently.
        row.Warranty = warranty is null ? null
            : WarrantyPricer.Value(
                warranty, resale?.ExpectedSale ?? 0m, quote.TotalCost, 0, 0, allowUplift: false);

        if (resale is not null && resale.HasPrice) ApplyResale(row, resale);

        // Refused: assorted contents, bulk with no stated count, for-parts, or a comp that failed
        // the retail cross-check. The reason reaches the seller instead of a number, because the
        // alternative on a lot is a fabricated figure multiplied by a unit count.
        if (quote.Economics.UnpriceableReason is { } reason)
        {
            row.Verdict = "no_data";
            row.VerdictNote = reason;
            ApplyDaysToCash(row, resale);
            return row;
        }

        if (resale is null || !resale.HasPrice)
        {
            row.Verdict = "no_data";
            row.VerdictNote = "No eBay sold history matched this lot.";
            ApplyDaysToCash(row, resale);
            return row;
        }

        row.EstimatedFees = quote.Fees;
        row.EstimatedShipCost = quote.ShipCost;
        row.NetProfit = quote.NetProfit;
        row.RoiPercent = quote.RoiPercent;
        row.MarginPercent = quote.MarginPercent;
        // The highest BID that still breaks even, with the premium and the tax already taken out of
        // it — so it is a number to bid to, not a budget to spend.
        row.MaxBuyPrice = quote.MaxBid;

        ApplyDaysToCash(row, resale);

        var compCount = resale.EvidenceCompCount;
        var (verdict, note) = Judge(quote.NetProfit, quote.RoiPercent, quote.TotalCost, compCount,
            resale.ConfidenceScore, resale.IdentityVerified);

        // A lot multiplies one comp by its unit count, so an error in that comp is multiplied too.
        // Its evidence bar rises with the unit count; under it, the row is a lead, never a green
        // badge. See LiquidationLotPricer.RequiredCompsForLot.
        if (verdict == "goldmine" && LiquidationLotPricer.EvidenceTooThinForLot(quote.Economics, compCount))
        {
            verdict = "thin";
            note = $"${quote.NetProfit:0.##} across {quote.Economics.Units} units — but on {compCount} sold comp" +
                   $"{(compCount == 1 ? "" : "s")}, and a lot multiplies whatever that comp gets wrong.";
        }

        if (row.Warranty is { } cover) (verdict, note) = WarrantyPricer.JudgeWarranty(verdict, note, cover);

        row.Verdict = verdict;
        // The bid moves. Saying where to stop is the only honest way to state a profit measured
        // against a price that has not finished changing.
        row.VerdictNote = $"{note} {LiquidationLotPricer.BidNote(quote.Economics, row.LocalAsk)}";
        return row;
    }

    /// <summary>
    /// The verdict on a free row, which the ordinary tiering gets wrong in one specific and
    /// expensive way: a $0 cost basis makes ROI unbounded, so <b>every free row clears the goldmine
    /// bar on return alone</b> and the badge stops distinguishing a free 65" television from a free
    /// mouse mat.
    /// </summary>
    /// <remarks>
    /// Three corrections, in the order they matter. All of them lower a verdict; none raises one,
    /// because "free" is already the most flattering cost basis there is and nothing here should
    /// make it more so.
    /// </remarks>
    public static (string Verdict, string Note) JudgeFreebie(
        string verdict, string note, FreebieEconomics freebie, decimal? netProfit)
    {
        // Already worthless or unpriceable: nothing to hold back.
        if (verdict is "pass" or "no_data") return (verdict, $"{note} {freebie.UrgencyNote}".Trim());

        // Free does not make a $6 flip worth a Saturday. Fetching it, photographing it, listing it
        // and packing it costs the same hour whatever it cost to buy.
        if (FreebiePricer.NotWorthCollecting(netProfit))
        {
            return ("thin",
                $"{netProfit:C} after fees and shipping — free, but not worth the trip and the packing " +
                $"once you've fetched it, photographed it and listed it. {freebie.UrgencyNote}".Trim());
        }

        // A cost the app couldn't see, large enough to change the answer: unstated delivery on a $0
        // item, or freight-sized stock priced by parcel comps. Said out loud rather than folded in.
        if (freebie.CappedReason is { } reason && verdict == "goldmine")
            return ("solid", $"{note} {reason} {freebie.UrgencyNote}".Trim());

        // The cost sentence only earns its place when there IS a cost. On a curb pickup it says
        // "nothing" at length, identically, on every row — which is noise in front of the deadline,
        // and the deadline is the part the seller has to act on.
        var cost = freebie.NetCost > 0m ? $"{freebie.CostNote} " : "";
        return (verdict, $"{note} {cost}{freebie.UrgencyNote}".Trim());
    }

    // The buy side of the same row. Every dollar this saves is profit with no fee, no shipping and
    // no wait attached — see NegotiationAdvisor.
    private static void ApplyNegotiation(LocalArbitrageOpportunity row, ResalePricing resale)
    {
        // An auction row never reaches here — BuildLiquidation returns before this — and for the
        // same reason: an auctioneer takes bids, not offers. What that row gets instead is the
        // highest bid worth making, which is the whole of its buy-side decision.
        //
        // Nobody at Walmart is reading your offer. Drafting one anyway would be the app's most
        // obviously useless output, and counting its "upside" would put money on the board that
        // cannot be won — so a retail row gets no plan, and the buy-side totals skip it. What the
        // seller does get is MaxBuyPrice above: the shelf price to stop at, which on retail is the
        // whole of the buy-side decision.
        if (row.IsRetail) return;

        // Nothing to haggle over at zero. Drafting an offer for a free item would be the app's most
        // absurd output, and counting its "upside" would put money on the board that was never on
        // it. What this row needs instead is speed — see FreebieEconomics.UrgencyNote.
        if (row.Freebie is not null) return;

        row.Negotiation = NegotiationAdvisor.Build(
            askPrice: row.LocalAsk,
            breakEvenBuyPrice: row.MaxBuyPrice ?? 0m,
            resalePrice: row.EbayExpectedSale ?? row.EbayResaleMedian,
            // The comps the advisor is allowed to cite in the message are the ones that priced it.
            // "Similar ones sold for $554" has to be true of the item being haggled over.
            compCount: resale.EvidenceCompCount,
            daysListed: DaysListed(row.PostedUtc),
            daysToCash: row.DaysToCash,
            originalPrice: row.OriginalPrice,
            distanceMiles: row.DistanceMiles);
    }

    // Only the sources that publish a real timestamp can answer this (Craigslist does, Facebook
    // doesn't). A missing date means the staleness argument simply isn't made — never that the
    // listing is fresh.
    private static int? DaysListed(DateTime? postedUtc)
    {
        if (postedUtc is not DateTime posted) return null;
        var days = (int)Math.Floor((DateTime.UtcNow - posted).TotalDays);
        return days < 0 ? null : days;
    }

    private static void ApplyDaysToCash(
        LocalArbitrageOpportunity row, ResalePricing? resale, int extraPipelineDays = 0)
    {
        var estimate = DaysToCashEstimator.Estimate(
            resale?.EstimatedDaysToSell, resale?.EstimatedMonthlySales ?? 0m,
            row.NetProfit, row.RoiPercent, extraPipelineDays);

        row.DaysToSell = estimate.DaysToSell;
        row.CashPipelineDays = estimate.PipelineDays;
        row.DaysToCash = estimate.DaysToCash;
        row.ProfitPerDay = estimate.ProfitPerDay;
        row.CapitalTurnsPerYear = estimate.CapitalTurnsPerYear;
        row.AnnualizedRoiPercent = estimate.AnnualizedRoiPercent;
        row.SpeedTier = estimate.SpeedTier;
        row.SpeedLabel = estimate.SpeedLabel;
        row.SpeedNote = estimate.Note;
    }

    // Which pricing sources actually contributed — "hosted comps + Terapeak" is a materially
    // stronger claim than either alone, so the row says which one it is rather than "eBay data".
    public static string SourceLabel(int soldCompCount, int terapeakCount) =>
        (soldCompCount > 0, terapeakCount > 0) switch
        {
            (true, true) => "hosted_comps+terapeak",
            (true, false) => "hosted_comps",
            (false, true) => "terapeak",
            _ => "none",
        };

    /// <summary>
    /// Pure verdict tiering. A free item has no cost basis so ROI is undefined rather than zero —
    /// treated as unbounded, which is what "free" actually means for a flip.
    /// </summary>
    /// <param name="compCount">
    /// Comps that actually PRICED it (<see cref="ResalePricing.EvidenceCompCount"/>), never the
    /// number the lookup returned. Passing the larger figure is how a one-comp valuation earns a
    /// badge that says twelve.
    /// </param>
    /// <param name="identityVerified">
    /// False when the item states a model/part number and no comp carries it. Nothing above "thin"
    /// is reachable from there at any comp count: the profit is arithmetic on another product's
    /// price. Defaulted so callers with no identity to check are unaffected.
    /// </param>
    public static (string Verdict, string Note) Judge(
        decimal netProfit, decimal? roiPercent, decimal localAsk, int compCount, int confidenceScore,
        bool identityVerified = true)
    {
        // Nothing was spent, so there is no ROI to quote — it is unbounded, not enormous. The
        // sentinel below stands in for that in the comparisons; it must never reach a sentence,
        // where it prints as a 29-digit percentage and reads as a bug in every number beside it.
        var costsNothing = localAsk <= 0m && roiPercent is null;

        if (netProfit <= 0)
        {
            return ("pass", costsNothing
                ? "Even at nothing, this doesn't clear eBay's cut and the cost of shipping it."
                : $"Sells for less than the ${localAsk:0.##} ask once fees are paid.");
        }

        var roi = roiPercent ?? (localAsk <= 0 ? decimal.MaxValue : 0m);

        // Priced off the wrong product. Checked before the count, because more comps of something
        // else is not more evidence — and stated as what it is, so the seller knows to re-search
        // rather than to go and buy it.
        if (!identityVerified)
        {
            return ("thin", $"${netProfit:0.##} on paper, but no sold comp carries this item's model or part " +
                            "number — the resale price behind it is another product's. Treat it as unpriced.");
        }

        if (compCount < ThinCompCount)
            return ("thin", $"Profitable on {compCount} sold comp{(compCount == 1 ? "" : "s")} — too few to trust yet.");

        if (roi >= GoldmineRoiPercent && netProfit >= GoldmineProfit
            && compCount >= GoldmineMinComps && confidenceScore >= GoldmineMinConfidence)
        {
            return ("goldmine", costsNothing
                ? $"${netProfit:0.##} net for nothing spent, backed by {compCount} sold comps."
                : $"${netProfit:0.##} net on a ${localAsk:0.##} buy, backed by {compCount} sold comps.");
        }

        if (roi >= SolidRoiPercent && netProfit >= SolidProfit)
        {
            return ("solid", costsNothing
                ? $"${netProfit:0.##} net after fees, and it cost nothing to buy."
                : $"${netProfit:0.##} net after fees ({roi:0}% ROI).");
        }

        return ("thin", confidenceScore < GoldmineMinConfidence
            ? $"${netProfit:0.##} net, but the sold data behind it is weak."
            : $"${netProfit:0.##} net — real, but a thin margin for the drive.");
    }

    // One comp lookup per product, not per tile. Keyed by the caller (a normalized product
    // signature), with the fullest title in each group used for the lookup.
    public static List<LocalArbitrageGroup> GroupByProduct(
        IEnumerable<LocalSupplyListing> listings, Func<LocalSupplyListing, string> keySelector)
    {
        var groups = new List<LocalArbitrageGroup>();
        var byKey = new Dictionary<string, LocalArbitrageGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var listing in listings)
        {
            if (string.IsNullOrWhiteSpace(listing.Title)) continue;
            // A blank key means the normalizer found nothing to key on — fall back to the title
            // so those listings stay separate instead of collapsing into one bogus group.
            var key = keySelector(listing);
            if (string.IsNullOrWhiteSpace(key)) key = listing.Title.Trim().ToLowerInvariant();

            if (!byKey.TryGetValue(key, out var group))
            {
                group = new LocalArbitrageGroup { Key = key, LookupTitle = listing.Title };
                byKey[key] = group;
                groups.Add(group);
            }
            group.Listings.Add(listing);
            if (listing.Title.Length > group.LookupTitle.Length) group.LookupTitle = listing.Title;
        }

        return groups;
    }

    // Rations the Terapeak scrape budget across products, because a real scrape is a browser
    // page load against a logged-in session — never something to spend once per search result.
    //
    // Corroborating a promising estimate comes first (that's the row someone is about to spend
    // money on); products the comps database couldn't price at all come second, biggest local
    // ask first, since an unpriced $900 item is worth a look and an unpriced $20 one is not.
    public static List<string> SelectScrapeTargets(
        IEnumerable<(string Key, decimal? PreliminaryProfit, bool HasTerapeak, decimal LocalAsk)> groups, int budget)
    {
        if (budget <= 0) return [];

        var candidates = groups.Where(g => !g.HasTerapeak).ToList();

        var promising = candidates
            .Where(g => g.PreliminaryProfit is > 0)
            .OrderByDescending(g => g.PreliminaryProfit!.Value)
            .Select(g => g.Key);

        var unpriced = candidates
            .Where(g => g.PreliminaryProfit is null)
            .OrderByDescending(g => g.LocalAsk)
            .Select(g => g.Key);

        return promising.Concat(unpriced).Take(budget).ToList();
    }

    // How the table comes back ordered. "profit" is the historical default and stays the default;
    // the other two exist because the biggest margin and the best use of the seller's cash are
    // routinely different rows — see DaysToCashEstimator.
    public const string SortByProfit = "profit";
    public const string SortByFastestCash = "fastest";        // shortest wait for the money
    public const string SortByProfitPerDay = "profit_per_day"; // most money earned per day tied up

    public static string NormalizeSort(string? sort) => (sort ?? "").Trim().ToLowerInvariant() switch
    {
        SortByFastestCash or "days" or "speed" => SortByFastestCash,
        SortByProfitPerDay or "perday" or "velocity" => SortByProfitPerDay,
        _ => SortByProfit,
    };

    // Best money first. Rows that couldn't be priced sort last rather than being dropped —
    // "we couldn't price this one" is information, and silently hiding listings from a
    // sourcing search is how a real deal gets missed.
    //
    // Every mode keeps that rule, and every mode keeps losers below winners: a listing that clears
    // its fees in 200 days is still a better row than one that never clears them at all, however
    // quickly it wouldn't.
    public static List<LocalArbitrageOpportunity> Rank(
        IEnumerable<LocalArbitrageOpportunity> rows, string? sort = null)
    {
        var ordered = rows
            .OrderByDescending(r => r.NetProfit.HasValue)
            .ThenByDescending(r => r.NetProfit > 0);

        return NormalizeSort(sort) switch
        {
            // Fastest cash back, then the bigger profit among rows that turn equally fast.
            SortByFastestCash => ordered
                .ThenBy(r => DaysToCashEstimator.SortableDaysToCash(r.DaysToCash))
                .ThenByDescending(r => r.NetProfit ?? 0m)
                .ThenBy(r => r.DistanceMiles ?? double.MaxValue)
                .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                .ToList(),

            // The most money per day of tied-up capital — a small fast flip can and should outrank
            // a bigger one that parks the cash for months.
            SortByProfitPerDay => ordered
                .ThenByDescending(r => r.ProfitPerDay.HasValue)
                .ThenByDescending(r => r.ProfitPerDay ?? 0m)
                .ThenByDescending(r => r.NetProfit ?? 0m)
                .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                .ToList(),

            _ => ordered
                .ThenByDescending(r => r.NetProfit ?? 0m)
                .ThenByDescending(r => r.RoiPercent ?? 0m)
                // Equal money on both rows: take the one that gives the cash back sooner.
                .ThenBy(r => DaysToCashEstimator.SortableDaysToCash(r.DaysToCash))
                .ThenBy(r => r.DistanceMiles ?? double.MaxValue)
                .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }
}
