using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Works out the Promoted Listings ad rate that leaves the most money in the seller's pocket — and
/// shows the tradeoff behind it, so the answer is checkable rather than something to take on faith.
/// </summary>
/// <remarks>
/// <para>
/// eBay's own suggested ad rate is computed from what other sellers in the category are paying. It
/// has no idea what this seller paid for the item, so it will happily suggest 12% on a listing whose
/// entire margin is 9% — and the fee is charged on the whole sale, shipping included, on every
/// ad-attributed sale. That is how Promoted Listings quietly turns a profitable listing into a
/// break-even one while the sales graph goes up.
/// </para>
/// <para>
/// The model has three moving parts and reports all three (<see cref="PromotedAssumptions"/>):
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Lift saturates.</b> <c>lift(r) = maxLift · r / (r + k)</c>. Doubling the rate does not double
/// the sales; the first points buy the most. <c>k</c> is the category's typical rate, because that
/// is the competitive floor — 2% in a category where the field runs 11% buys almost no placement,
/// and the same 2% in a 4.5% category is a real bid.
/// </description></item>
/// <item><description>
/// <b>You pay for sales you were getting anyway.</b> A buyer who would have found the listing
/// regardless still arrives through the ad, and eBay bills the rate on that sale. That share
/// (<c>c</c>) is the reason a small ad rate is not free, and it is why the break-even lift is never
/// zero.
/// </description></item>
/// <item><description>
/// <b>The break-even lift is model-free.</b> <c>L* = c·f / (n − f)</c> falls out of the arithmetic
/// with no lift curve involved, so it stays true even if the curve is wrong. It is shown on every
/// rung of the ladder next to what the curve expects, so the seller can see whether the
/// recommendation depends on the model or survives without it.
/// </description></item>
/// </list>
/// <para>
/// Net profit per sale is not re-derived here: it comes from the same <see cref="ProfitCalculator"/>
/// and <see cref="FeeProfile"/> pair that <see cref="NetProceedsCalculator"/>, the inventory
/// repricer and the watcher-offer floors use, with the ad rate zeroed so the ad fee is the only
/// thing this varies. A dollar of margin is the same dollar whichever screen is asking.
/// </para>
/// </remarks>
public sealed class PromotedListingAdvisor(ProfitCalculator profitCalc)
{
    // ── The rungs the tradeoff is shown at ───────────────────────────────────────────────────
    private static readonly decimal[] LadderRungs = [0m, 2m, 3m, 4m, 5m, 6m, 7m, 8m, 10m, 12m, 15m, 20m];

    /// <summary>Rates are searched and reported at half-point resolution — a decision, not false precision.</summary>
    public const decimal RateStep = 0.5m;

    /// <summary>Below this the two rates are the same answer, and churning a campaign changes nothing.</summary>
    public const decimal MeaningfulRateChange = 1m;

    /// <summary>
    /// And below this the money is the same answer too. A dime per hundred sales is not a reason to
    /// go and edit a campaign — the same materiality rule the repricer applies to a one-cent markdown.
    /// </summary>
    public const decimal MinGainPer100Sales = 10m;

    /// <summary>A listing priced this far over the market has a price problem, not a visibility problem.</summary>
    public const decimal OverMarketPercent = 15m;

    /// <summary>Sold comps needed before the market counts as evidence that this item moves at all.</summary>
    public const int MinCompsForEvidence = 3;

    /// <summary>Everything one listing needs to be advised, from the board or from the editor.</summary>
    public sealed record Input(
        string Title,
        decimal ListPrice,
        decimal? UnitCost,
        decimal BuyerPaidShipping = 0m,
        decimal? ShippingCostOverride = null,
        string Category = "",
        decimal? CategoryRateOverride = null,
        decimal CurrentRatePercent = 0m,
        decimal? SalesPerMonth = null,
        int? DaysListed = null,
        int WatchCount = 0,
        int QuantitySold = 0,
        int SoldCompCount = 0,
        int LiquidityScore = 0,
        string LiquidityLevel = "",
        decimal? MarketPrice = null,
        decimal? PriceGapPercent = null,
        bool MarketComparable = true,
        string ListingId = "",
        string Sku = "",
        string Url = "",
        string ImageUrl = "");

    // ── The pure math ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Extra sales an ad rate is expected to buy, as a percentage of the listing's organic sales.
    /// Saturating: at the category's typical rate it delivers half the ceiling, and the curve keeps
    /// climbing but never gets there, which is why "just bid higher" stops working.
    /// </summary>
    public static decimal LiftPercentAt(decimal ratePercent, decimal maxLiftPercent, decimal halfLiftRatePercent)
    {
        if (ratePercent <= 0m || maxLiftPercent <= 0m) return 0m;
        var k = Math.Max(0.5m, halfLiftRatePercent);
        return Math.Round(maxLiftPercent * ratePercent / (ratePercent + k), 2);
    }

    /// <summary>
    /// The extra sales a rate must actually deliver to leave the seller no worse off.
    /// </summary>
    /// <remarks>
    /// Solving <c>L·n = (c + L)·f</c> gives <c>L = c·f / (n − f)</c>. No lift curve appears in it,
    /// which is the point: this number is true whatever the model thinks. Null means the ad fee is
    /// at or above the entire profit on the sale, so no amount of extra volume can pay for it — the
    /// one case where "spend more, sell more" is arithmetically hopeless.
    /// </remarks>
    public static decimal? BreakEvenLiftPercent(decimal adFeePerSale, decimal? netPerSaleNoAds, decimal cannibalizationPercent)
    {
        if (netPerSaleNoAds is not decimal net || net <= 0m) return null;
        if (adFeePerSale <= 0m) return 0m;
        if (adFeePerSale >= net) return null;

        var c = Math.Clamp(cannibalizationPercent, 0m, 100m) / 100m;
        return Math.Round(c * adFeePerSale / (net - adFeePerSale) * 100m, 1);
    }

    /// <summary>
    /// The rate at which the ad fee equals the whole profit on the sale. Above it every
    /// ad-attributed sale is made at a loss, however many of them there are.
    /// </summary>
    public static decimal? MarginCeilingRatePercent(decimal? netPerSaleNoAds, decimal grossPerSale)
    {
        if (netPerSaleNoAds is not decimal net || net <= 0m || grossPerSale <= 0m) return null;
        return Math.Round(net / grossPerSale * 100m, 1);
    }

    /// <summary>
    /// Take-home across 100 sales the listing would have made anyway, at a given ad rate.
    /// </summary>
    /// <remarks>
    /// <c>100 · [ (1 + L)·n − (c + L)·f ]</c> — volume sold times profit per sale, minus the ad fee
    /// on every sale eBay attributes to the ad (the incremental ones plus the share of the organic
    /// ones the ad intercepted). Expressed per 100 organic sales because the rate that maximises it
    /// does not depend on how many units the listing actually moves — so the recommendation stands
    /// even for a listing that has never sold, where any monthly projection would be invented.
    /// </remarks>
    public static decimal? NetPer100Sales(
        decimal? netPerSaleNoAds, decimal adFeePerSale, decimal liftPercent, decimal cannibalizationPercent)
    {
        if (netPerSaleNoAds is not decimal net) return null;

        var lift = Math.Max(0m, liftPercent) / 100m;
        var c = Math.Clamp(cannibalizationPercent, 0m, 100m) / 100m;
        return Math.Round(100m * ((1m + lift) * net - (c + lift) * adFeePerSale), 2);
    }

    /// <summary>The ad fee eBay bills on one sale at this rate — charged on item price plus shipping.</summary>
    public static decimal AdFeeAt(decimal ratePercent, decimal grossPerSale) =>
        Math.Round(Math.Max(0m, grossPerSale) * Math.Max(0m, ratePercent) / 100m, 2);

    /// <summary>
    /// How much room ads have on this listing, and how much of the bill is for sales it was already
    /// making. Both are assumptions; both are reported to the seller rather than hidden in the math.
    /// </summary>
    public static PromotedAssumptions AssumptionsFor(Input input, decimal categoryRatePercent)
    {
        var proven = input.QuantitySold > 0;
        var fast = input.SalesPerMonth >= 2m || input.LiquidityLevel == "Fast Mover";
        var aged = input.DaysListed >= 60;
        var marketMoves = input.SoldCompCount >= MinCompsForEvidence || input.LiquidityScore >= 40;

        // Ads buy attention, not demand. The ceiling is highest where there is proven demand for the
        // product and this listing is not getting any of it, and lowest where the listing is already
        // being found — you cannot buy much more of what you already have.
        var (maxLift, basis) = (fast, proven, aged, marketMoves) switch
        {
            (true, _, _, _)          => (20m, "This one already sells, so there is less attention left to buy."),
            (_, true, _, _)          => (28m, "It has sold before, so buyers are finding it without help."),
            (_, _, true, true)       => (45m, "Real sold history for this product, and this listing is not getting any of it — visibility is the likely blocker."),
            (_, _, _, true)          => (35m, "Sold comps show the product moves; ads compete for the buyers already searching."),
            _                        => (25m, "No sold history to confirm buyers want this, so the lift is assumed conservative."),
        };

        // The quiet cost. The better a listing already converts, the more of its own sales arrive
        // through the ad and get billed — which is exactly why a fast seller should pay less, not
        // more, however tempting eBay's suggested rate looks.
        var cannibalization = (fast, proven, aged) switch
        {
            (true, _, _)     => 65m,
            (_, true, _)     => 55m,
            (_, false, true) => 35m,
            _                => 50m,
        };
        if (input.WatchCount >= 10) cannibalization += 5m;

        return new PromotedAssumptions
        {
            MaxLiftPercent = maxLift,
            HalfLiftRatePercent = Math.Max(0.5m, categoryRatePercent),
            CannibalizationPercent = Math.Clamp(cannibalization, 25m, 75m),
            Basis = basis,
        };
    }

    /// <summary>
    /// The rate that maximises take-home, found by walking every half point rather than by calculus
    /// — the ladder the seller is shown is the same set of numbers the search ran on.
    /// </summary>
    /// <param name="minNetPerSale">
    /// The seller's own per-sale profit floor from Fees &amp; Costs. An ad rate spends margin exactly
    /// like a markdown does, so the same floor that bounds the repricer and the watcher offers bounds
    /// this too — otherwise a seller who said "never under $15 a sale" gets talked into $9 by a
    /// campaign instead of by a price.
    /// </param>
    public static (decimal Rate, bool FloorLimited, bool CapLimited) SearchBestRate(
        decimal? netPerSaleNoAds, decimal grossPerSale, PromotedAssumptions assumptions,
        decimal maxRatePercent, decimal minNetPerSale = 0m)
    {
        if (netPerSaleNoAds is not decimal net || net <= 0m || grossPerSale <= 0m) return (0m, false, false);

        var ceiling = Math.Min(Math.Max(0m, maxRatePercent), PromotedRateNorms.MaxRecommendedRatePercent);
        var best = 0m;
        var bestValue = NetPer100Sales(net, 0m, 0m, assumptions.CannibalizationPercent) ?? 0m;

        var highestAllowed = 0m;      // the last rate the floor let us look at
        var stoppedByFloor = false;

        for (var rate = PromotedRateNorms.EbayMinimumRatePercent; rate <= ceiling + 0.0001m; rate += RateStep)
        {
            var fee = AdFeeAt(rate, grossPerSale);
            // Net per sale falls monotonically as the rate climbs, so the first rate that breaches
            // the seller's stated floor rules out every rate above it as well.
            if (minNetPerSale > 0m && net - fee < minNetPerSale)
            {
                stoppedByFloor = true;
                break;
            }
            highestAllowed = rate;

            var lift = LiftPercentAt(rate, assumptions.MaxLiftPercent, assumptions.HalfLiftRatePercent);
            var value = NetPer100Sales(net, fee, lift, assumptions.CannibalizationPercent) ?? 0m;
            if (value <= bestValue) continue;

            bestValue = value;
            best = rate;
        }

        // Both of these mean the same thing and are worth saying out loud: the answer was set by a
        // boundary rather than by the math, so the seller knows there is a decision behind it.
        var floorLimited = stoppedByFloor && best > 0m && best >= highestAllowed - 0.0001m;
        var capLimited = !stoppedByFloor && best > 0m && best >= ceiling - 0.0001m;

        return (best, floorLimited, capLimited);
    }

    /// <summary>The tradeoff table: what each rate costs, what it must buy, and what it is worth.</summary>
    public static List<AdRatePoint> BuildLadder(
        decimal? netPerSaleNoAds, decimal grossPerSale, PromotedAssumptions assumptions,
        decimal currentRatePercent, decimal? recommendedRatePercent)
    {
        var rates = new List<decimal>(LadderRungs);
        if (currentRatePercent > 0m) rates.Add(Math.Round(currentRatePercent, 1));
        if (recommendedRatePercent is decimal rec && rec > 0m) rates.Add(Math.Round(rec, 1));

        var baseline = NetPer100Sales(netPerSaleNoAds, 0m, 0m, assumptions.CannibalizationPercent);

        return [.. rates.Distinct().OrderBy(r => r).Select(rate =>
        {
            var fee = AdFeeAt(rate, grossPerSale);
            var lift = LiftPercentAt(rate, assumptions.MaxLiftPercent, assumptions.HalfLiftRatePercent);
            var net100 = NetPer100Sales(netPerSaleNoAds, fee, lift, assumptions.CannibalizationPercent);

            return new AdRatePoint
            {
                RatePercent = rate,
                AdFeePerSale = fee,
                NetPerSale = netPerSaleNoAds is decimal n ? Math.Round(n - fee, 2) : null,
                BreakEvenLiftPercent = BreakEvenLiftPercent(fee, netPerSaleNoAds, assumptions.CannibalizationPercent),
                ModeledLiftPercent = lift,
                NetPer100Sales = net100,
                NetChangePer100 = net100 is decimal v && baseline is decimal b ? Math.Round(v - b, 2) : null,
                IsRecommended = recommendedRatePercent is decimal r && Math.Abs(r - rate) < 0.01m,
                IsCurrent = Math.Abs(currentRatePercent - rate) < 0.01m,
                AboveCeiling = netPerSaleNoAds is decimal net2 && net2 > 0m && fee >= net2,
            };
        })];
    }

    // ── Building one advised listing ─────────────────────────────────────────────────────────

    /// <summary>
    /// The full recommendation for one listing: what it nets today, what each rate costs it, the
    /// rate to run, and why.
    /// </summary>
    public PromotedAdvice Build(Input input, FeeProfile fees)
    {
        var category = input.CategoryRateOverride is decimal seller
            ? PromotedRateNorms.Override(seller, input.Category)
            : PromotedRateNorms.Resolve(input.Category);

        var gross = Math.Round(Math.Max(0m, input.ListPrice) + Math.Max(0m, input.BuyerPaidShipping), 2);
        var currentRate = Math.Clamp(input.CurrentRatePercent, 0m, 100m);

        var advice = new PromotedAdvice
        {
            ListingId = input.ListingId,
            Sku = input.Sku,
            Title = input.Title,
            Url = input.Url,
            ImageUrl = input.ImageUrl,
            ListPrice = Math.Max(0m, input.ListPrice),
            BuyerPaidShipping = Math.Max(0m, input.BuyerPaidShipping),
            GrossPerSale = gross,
            UnitCost = input.UnitCost,
            HasCostBasis = input.UnitCost is > 0m,
            Category = input.Category,
            CategoryLabel = category.Label,
            CategoryRatePercent = category.TypicalRatePercent,
            CategoryCompetition = category.Competition,
            CategoryBasis = category.Basis,
            CurrentRatePercent = currentRate,
            AdFeeAtCurrent = AdFeeAt(currentRate, gross),
            DaysListed = input.DaysListed,
            WatchCount = input.WatchCount,
            QuantitySold = input.QuantitySold,
            SalesPerMonth = input.SalesPerMonth,
            SoldCompCount = input.SoldCompCount,
            LiquidityScore = input.LiquidityScore,
            LiquidityLevel = input.LiquidityLevel,
            MarketPrice = input.MarketPrice,
            PriceGapPercent = input.PriceGapPercent,
            MarketComparable = input.MarketComparable,
            EvidenceLevel = input.QuantitySold > 0 ? "proven"
                : input.SoldCompCount >= MinCompsForEvidence ? "market" : "thin",
        };

        var assumptions = AssumptionsFor(input, category.TypicalRatePercent);
        advice.Assumptions = assumptions;

        if (advice.ListPrice <= 0m)
        {
            advice.Verdict = "no_price";
            advice.Headline = "No price to advertise against";
            advice.Note = "eBay reported no price for this listing, so there is nothing to size an ad rate from.";
            return advice;
        }

        // ── What one sale is worth before a cent of ad spend ─────────────────────────────────
        // The ad rate is zeroed on a copy of the profile so the fee this varies is the only one
        // that moves. Everything else — eBay's cut, the label, packaging, the return reserve — is
        // the seller's own configured cost of doing business, unchanged.
        if (advice.HasCostBasis)
        {
            var noAds = fees.Clone();
            noAds.PromotedListingRatePercent = 0m;
            var breakdown = profitCalc.Calculate(
                supplierUnitCost: input.UnitCost!.Value, quantity: 1,
                expectedSalePrice: advice.ListPrice, quickSalePrice: advice.ListPrice,
                buyerPaidShipping: advice.BuyerPaidShipping, fees: noAds,
                actualShippingCostOverride: input.ShippingCostOverride);

            advice.NetPerSaleNoAds = breakdown.NetProfitPerUnit;
            advice.MarginPercent = breakdown.MarginPercent;
        }

        advice.MaxSustainableRatePercent = MarginCeilingRatePercent(advice.NetPerSaleNoAds, gross);
        advice.NetPerSaleAtCurrent = advice.NetPerSaleNoAds is decimal n0
            ? Math.Round(n0 - advice.AdFeeAtCurrent, 2) : null;
        advice.BreakEvenLiftAtCurrentPercent =
            BreakEvenLiftPercent(advice.AdFeeAtCurrent, advice.NetPerSaleNoAds, assumptions.CannibalizationPercent);
        advice.NetPer100AtCurrent = NetPer100Sales(
            advice.NetPerSaleNoAds, advice.AdFeeAtCurrent,
            LiftPercentAt(currentRate, assumptions.MaxLiftPercent, assumptions.HalfLiftRatePercent),
            assumptions.CannibalizationPercent);

        if (!advice.HasCostBasis)
        {
            advice.Ladder = BuildLadder(null, gross, assumptions, currentRate, null);
            advice.Verdict = "no_cost_basis";
            advice.Headline = $"{Money(AdFeeAt(category.TypicalRatePercent, gross))} per sale at the {category.TypicalRatePercent:0.#}% {category.Label} rate";
            advice.Note = "What that costs you depends on what you paid for the item, and that isn't recorded. "
                        + "Add it in the listing editor or Inventory Health and this sizes the rate against your real margin.";
            return advice;
        }

        if (advice.NetPerSaleNoAds is not decimal net || net <= 0m)
        {
            advice.RecommendedRatePercent = 0m;
            advice.AdFeeAtRecommended = 0m;
            advice.NetPerSaleAtRecommended = advice.NetPerSaleNoAds;
            advice.Ladder = BuildLadder(advice.NetPerSaleNoAds, gross, assumptions, currentRate, 0m);
            advice.Verdict = "no_margin";
            advice.Headline = "Don't advertise this one";
            advice.Note = $"At {Money(advice.ListPrice)} this sale " +
                (advice.NetPerSaleNoAds is decimal loss && loss < 0m
                    ? $"already loses {Money(Math.Abs(loss))} before any ad spend."
                    : "makes nothing before any ad spend.")
                + " An ad rate only makes each sale worse — the price or the cost has to move first.";
            advice.Signals.Add("Promoted Listings multiplies whatever the margin already is. On a loss, it multiplies the loss.");
            return advice;
        }

        // ── The search ───────────────────────────────────────────────────────────────────────
        // Thin evidence does not earn an aggressive rate. Above the category norm the seller is
        // outbidding the field, which is a bet worth making on a listing with proven demand and not
        // on one the app knows nothing about.
        var rateCap = PromotedRateNorms.MaxRecommendedRatePercent;
        var thinEvidence = advice.EvidenceLevel == "thin";
        if (thinEvidence) rateCap = Math.Min(rateCap, category.TypicalRatePercent);

        var overMarket = advice.MarketComparable && advice.PriceGapPercent is decimal gap && gap > OverMarketPercent;
        if (overMarket) rateCap = Math.Min(rateCap, category.TypicalRatePercent);

        var (best, floorLimited, capLimited) =
            SearchBestRate(net, gross, assumptions, rateCap, fees.MinimumNetProfit);

        advice.RecommendedRatePercent = best;
        advice.AdFeeAtRecommended = AdFeeAt(best, gross);
        advice.NetPerSaleAtRecommended = Math.Round(net - advice.AdFeeAtRecommended.Value, 2);
        advice.BreakEvenLiftAtRecommendedPercent =
            BreakEvenLiftPercent(advice.AdFeeAtRecommended.Value, net, assumptions.CannibalizationPercent);
        advice.ModeledLiftAtRecommendedPercent =
            LiftPercentAt(best, assumptions.MaxLiftPercent, assumptions.HalfLiftRatePercent);
        advice.NetPer100AtRecommended = NetPer100Sales(
            net, advice.AdFeeAtRecommended.Value, advice.ModeledLiftAtRecommendedPercent.Value,
            assumptions.CannibalizationPercent);
        advice.NetGainPer100 = advice.NetPer100AtRecommended is decimal a && advice.NetPer100AtCurrent is decimal b
            ? Math.Round(a - b, 2) : null;
        advice.AdFeeChangePerSale = Math.Round(advice.AdFeeAtRecommended.Value - advice.AdFeeAtCurrent, 2);
        advice.Ladder = BuildLadder(net, gross, assumptions, currentRate, best);

        // Optimal and worth doing are different questions. On a $12 item the best rate can beat the
        // current one by pennies a hundred sales, and a board that lists that as a task is a board
        // nobody finishes.
        var materialGain = Math.Max(MinGainPer100Sales, Math.Abs(advice.NetPer100AtCurrent ?? 0m) * 0.01m);
        advice.ChangeWorthMaking = advice.NetGainPer100 is decimal gain && gain >= materialGain;

        // Monthly money only where the listing's own history supports it. eBay reports a cumulative
        // sold count with no dates, so this is a lifetime average — and for a listing that has never
        // sold there is no rate at all, which is reported as "per sale" figures rather than as a
        // projection nobody can stand behind.
        if (input.SalesPerMonth is decimal units && units > 0m)
        {
            advice.ExtraProfitPerMonth = advice.NetGainPer100 is decimal monthlyGain
                ? Math.Round(monthlyGain / 100m * units, 2) : null;
            advice.AdSpendPerMonthAtRecommended = Math.Round(
                units * (assumptions.CannibalizationPercent + advice.ModeledLiftAtRecommendedPercent!.Value) / 100m
                * advice.AdFeeAtRecommended.Value, 2);
            advice.AdSpendPerMonthAtCurrent = Math.Round(
                units * (assumptions.CannibalizationPercent
                         + LiftPercentAt(currentRate, assumptions.MaxLiftPercent, assumptions.HalfLiftRatePercent)) / 100m
                * advice.AdFeeAtCurrent, 2);
        }

        // ── Signals ──────────────────────────────────────────────────────────────────────────
        if (thinEvidence)
            advice.Signals.Add($"No sold history for this item yet, so the rate is held at the {category.TypicalRatePercent:0.#}% category norm rather than bid above the field on a guess.");
        if (overMarket)
            advice.Signals.Add($"Listed {advice.PriceGapPercent:0.#}% above the {Money(advice.MarketPrice ?? 0m)} going rate. Ads put a price buyers are already beating in front of more of them — fix the price first, then bid.");
        if (floorLimited)
            advice.Signals.Add($"Held back by your {Money(fees.MinimumNetProfit)} minimum profit per sale — a higher rate would spend past the floor you set in Fees & Costs.");
        if (capLimited && best >= PromotedRateNorms.MaxRecommendedRatePercent)
            advice.Signals.Add($"The math still improves above {PromotedRateNorms.MaxRecommendedRatePercent:0}%, but a rate that size is a clearance decision to make deliberately, not a default.");
        if (advice.MaxSustainableRatePercent is decimal ceiling)
            advice.Signals.Add($"Above {ceiling:0.#}% the ad fee is bigger than the whole profit on the sale — no amount of extra volume fixes that.");
        if (input.QuantitySold > 0 && best < currentRate)
            advice.Signals.Add($"This listing has already sold {input.QuantitySold} unit{(input.QuantitySold == 1 ? "" : "s")}. Buyers find it without paying, and eBay bills the ad rate on those sales too.");

        (advice.Verdict, advice.Headline, advice.Note) = Describe(advice, category, assumptions, overMarket);
        return advice;
    }

    // The verdict carries the colour and the copy together, so "you are overpaying" can never be
    // rendered in the same style as "this is right".
    private static (string Verdict, string Headline, string Note) Describe(
        PromotedAdvice a, CategoryAdRate category, PromotedAssumptions assumptions, bool overMarket)
    {
        var rec = a.RecommendedRatePercent ?? 0m;
        var lift = a.BreakEvenLiftAtRecommendedPercent;

        if (rec <= 0m)
            return ("dont_promote",
                a.CurrentRatePercent > 0m
                    ? $"Turn the ads off — {Money(a.AdFeeAtCurrent)} a sale for nothing"
                    : "Not worth promoting",
                $"Every rate costs more in fees than it can buy back. At {Money(a.NetPerSaleNoAds ?? 0m)} profit on a "
              + $"{Money(a.GrossPerSale)} sale, even a {PromotedRateNorms.EbayMinimumRatePercent:0}% rate takes "
              + $"{Money(AdFeeAt(PromotedRateNorms.EbayMinimumRatePercent, a.GrossPerSale))} of it — and you pay that on "
              + $"the sales you were already making. Keep the margin.");

        // Optimal but not worth the trip. Said plainly rather than dressed up as a task, because a
        // board full of dime-sized "wins" is how the real ones get scrolled past.
        if (!a.ChangeWorthMaking && Math.Abs(a.CurrentRatePercent - rec) >= MeaningfulRateChange)
            return ("on_target", $"Leave it at {a.CurrentRatePercent:0.#}%",
                $"{rec:0.#}% is fractionally better on paper — about {Money(a.NetGainPer100 ?? 0m)} per 100 sales of this "
              + "item. That is not worth a trip to Seller Hub; the money on this listing is in the price, not the ad rate.");

        var liftWords = lift is decimal l
            ? $"It has to lift sales {l:0.#}% to pay for itself; the model expects {a.ModeledLiftAtRecommendedPercent:0.#}%."
            : "";

        if (overMarket)
            return ("fix_price_first", $"Fix the price, then run {rec:0.#}%",
                $"At {rec:0.#}% you'd pay {Money(a.AdFeeAtRecommended ?? 0m)} per sale, leaving "
              + $"{Money(a.NetPerSaleAtRecommended ?? 0m)}. But this is listed above what the market is paying, and "
              + "ads cannot sell a price buyers are already beating. " + liftWords);

        if (a.CurrentRatePercent - rec >= MeaningfulRateChange)
            return ("over_promoted", $"Drop to {rec:0.#}% — you're overpaying by {Money(Math.Abs(a.AdFeeChangePerSale))} a sale",
                $"{a.CurrentRatePercent:0.#}% costs {Money(a.AdFeeAtCurrent)} of a {Money(a.NetPerSaleNoAds ?? 0m)} margin, and it has to lift "
              + $"sales {a.BreakEvenLiftAtCurrentPercent:0.#}% just to stand still. {rec:0.#}% keeps "
              + $"{Money(a.NetPerSaleAtRecommended ?? 0m)} per sale instead of {Money(a.NetPerSaleAtCurrent ?? 0m)}"
              + (a.NetGainPer100 is decimal g && g > 0m ? $" — {Money(g)} more per 100 sales." : "."));

        if (rec - a.CurrentRatePercent >= MeaningfulRateChange)
            return ("under_promoted", $"Raise to {rec:0.#}%",
                $"The margin can carry it: {Money(a.AdFeeAtRecommended ?? 0m)} of ads still leaves "
              + $"{Money(a.NetPerSaleAtRecommended ?? 0m)} per sale, against {Money(a.NetPerSaleNoAds ?? 0m)} unpromoted. "
              + $"{category.Label} typically pays {category.TypicalRatePercent:0.#}% ({category.Competition} competition), so "
              + $"{a.CurrentRatePercent:0.#}% is close to invisible. " + liftWords);

        return ("on_target", $"{rec:0.#}% is right for this one",
            $"{Money(a.AdFeeAtRecommended ?? 0m)} per sale out of {Money(a.NetPerSaleNoAds ?? 0m)} of margin, leaving "
          + $"{Money(a.NetPerSaleAtRecommended ?? 0m)}. {liftWords} {assumptions.Basis}".TrimEnd());
    }

    // ── The board ────────────────────────────────────────────────────────────────────────────

    /// <summary>Biggest money first: whichever listings the rate is most wrong on, in dollars.</summary>
    public static List<PromotedAdvice> Rank(IEnumerable<PromotedAdvice> items) =>
        [.. items
            .OrderByDescending(i => i.NeedsChange)
            .ThenByDescending(i => Math.Abs(i.NetGainPer100 ?? 0m))
            .ThenByDescending(i => i.AdFeeAtCurrent)
            .ThenByDescending(i => i.ListPrice)
            .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)];

    /// <summary>What the whole board is spending, and what the recommended rates would do to it.</summary>
    public static PromotedBoardSummary Summarize(IReadOnlyList<PromotedAdvice> items)
    {
        var advised = items.Where(i => i.HasRecommendation).ToList();
        var withVolume = advised.Where(i => i.ExtraProfitPerMonth.HasValue).ToList();
        var revenue = advised.Sum(i => i.GrossPerSale);

        return new PromotedBoardSummary
        {
            ListingsAnalyzed = items.Count,
            WithCostBasis = items.Count(i => i.HasCostBasis),
            Advised = advised.Count,

            UnderPromoted = advised.Count(i => i.Verdict == "under_promoted"),
            OverPromoted = advised.Count(i => i.Verdict == "over_promoted"),
            OnTarget = advised.Count(i => i.Verdict == "on_target"),
            ShouldNotPromote = items.Count(i => i.Verdict is "dont_promote" or "no_margin"),

            // One sale of each, not a projected month: eBay publishes no per-listing sales rate, and
            // a fabricated volume would put a made-up dollar sign on the headline figure.
            AdFeePerRoundAtCurrent = Math.Round(items.Sum(i => i.AdFeeAtCurrent), 2),
            AdFeePerRoundAtRecommended = Math.Round(advised.Sum(i => i.AdFeeAtRecommended ?? 0m)
                                                  + items.Where(i => !i.HasRecommendation).Sum(i => i.AdFeeAtCurrent), 2),
            OverspendPerRound = Math.Round(advised
                .Where(i => i.Verdict == "over_promoted" || i.Verdict == "dont_promote")
                .Sum(i => Math.Max(0m, i.AdFeeAtCurrent - (i.AdFeeAtRecommended ?? 0m))), 2),

            NetGainPer100 = Math.Round(advised.Sum(i => Math.Max(0m, i.NetGainPer100 ?? 0m)), 2),
            // Weighted by what each listing sells for: a $1,400 miner's rate matters more to the
            // blended default than a $9 cable's does.
            BlendedRecommendedPercent = revenue > 0m
                ? Math.Round(advised.Sum(i => (i.RecommendedRatePercent ?? 0m) * i.GrossPerSale) / revenue, 1)
                : null,

            WithSalesHistory = withVolume.Count,
            ExtraProfitPerMonth = Math.Round(withVolume.Sum(i => i.ExtraProfitPerMonth ?? 0m), 2),
        };
    }

    private static string Money(decimal value) =>
        value.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
}
