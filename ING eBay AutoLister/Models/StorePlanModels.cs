namespace ING_eBay_AutoLister.Models;

// The Store Plan. Every other money screen in this app prices a decision about an ITEM — what to
// buy it for, what to list it at, what to accept for it. This one prices the only eBay decision
// that charges the seller whether they sell anything or not: which Store subscription they are on.
//
// It is the cheapest money in the app to find. The seller does nothing, changes no listing and
// takes no risk; they click one radio button on eBay and the bill goes down every month from then
// on. A seller with 900 live listings and no Store subscription is paying eBay roughly $227 a month
// in insertion fees to keep them there. A Basic Store is $21.95 and covers 1,000. That is over
// $2,400 a year, sitting still, and nothing else on the market — not Vendoo, not List Perfectly,
// not ZIK — tells them, because none of them can see how many listings the seller keeps live.
//
// What this deliberately does NOT do is model final value fees. Those are set per category, eBay
// publishes no per-account rate, and a comparison that guessed at them would move the recommendation
// on a number nobody can check. The three things the tiers genuinely differ on — the subscription,
// the free-listing allotment, and the insertion fee after it — are all published, all checkable,
// and are enough to decide the question on their own.

/// <summary>One eBay Store subscription tier as eBay publishes it for the US site.</summary>
/// <remarks>
/// "No store" is a tier here rather than a special case. It has a price ($0), an allotment (eBay's
/// standard 250 zero-insertion-fee listings a month) and an insertion fee after it, so it compares
/// on exactly the same three numbers as the paid tiers — and for most small sellers it wins.
/// </remarks>
public sealed class StorePlanTier
{
    /// <summary>Stable identifier — this is what gets persisted, so it must not track the name.</summary>
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Cost per month when billed monthly. Null where eBay sells the tier annually only.</summary>
    public decimal? MonthlyBilling { get; set; }

    /// <summary>Cost per month when billed annually — never more than <see cref="MonthlyBilling"/>.</summary>
    public decimal AnnualBilling { get; set; }

    /// <summary>Fixed-price listings a month that carry no insertion fee.</summary>
    public int FreeListings { get; set; }

    /// <summary>What each listing past the allotment costs to list, and to renew.</summary>
    public decimal InsertionFeeAfter { get; set; }

    /// <summary>eBay sells this tier on an annual commitment only.</summary>
    public bool AnnualBillingOnly => MonthlyBilling is null;

    /// <summary>What the tier gives beyond listings — never priced, only stated.</summary>
    public string Unlocks { get; set; } = "";
}

/// <summary>One tier, costed against the number of listings this seller actually keeps live.</summary>
public sealed class StorePlanOption
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>The subscription per month on the billing cycle being compared.</summary>
    public decimal Subscription { get; set; }
    public int FreeListings { get; set; }
    public decimal InsertionFeeAfter { get; set; }

    /// <summary>Listings past the allotment — the ones that are actually billed.</summary>
    public int ListingsCharged { get; set; }
    public decimal InsertionCost { get; set; }

    public decimal MonthlyCost { get; set; }
    public decimal AnnualCost { get; set; }

    /// <summary>Against the plan the seller is on today. Negative is a saving.</summary>
    public decimal MonthlyDelta { get; set; }

    public bool IsCurrent { get; set; }
    public bool IsBest { get; set; }
    public bool AnnualBillingOnly { get; set; }

    /// <summary>The listing count from which this tier is the cheapest of them all.</summary>
    public int CheapestFrom { get; set; }

    /// <summary>The count past which something else takes over. Null means nothing ever does.</summary>
    public int? CheapestTo { get; set; }

    /// <summary>True when no listing count makes this tier the cheapest — it is always beaten.</summary>
    public bool NeverCheapest { get; set; }

    /// <summary>The arithmetic, in words, so the row is checkable rather than asserted.</summary>
    public string Basis { get; set; } = "";

    public string Unlocks { get; set; } = "";
}

/// <summary>Everything the optimizer needs. Assembled by the endpoint; nothing here is read twice.</summary>
public sealed class StorePlanInputs
{
    /// <summary>Live fixed-price listings on the account, as counted from eBay.</summary>
    public int ActiveListings { get; set; }

    /// <summary>True when <see cref="ActiveListings"/> came from eBay rather than from the seller.</summary>
    public bool ListingCountMeasured { get; set; }

    /// <summary>A count the seller typed — to plan a scale-up, or because eBay is not connected.</summary>
    public int? ListingsOverride { get; set; }

    public string CurrentPlanKey { get; set; } = "";
    public bool AnnualBilling { get; set; } = true;

    /// <summary>Average monthly gross sales, for scale. Zero when nothing is on record.</summary>
    public decimal MonthlySales { get; set; }
}

/// <summary>What the Store Plan screen renders.</summary>
public sealed class StorePlanResult
{
    /// <summary>"ok", or "ebay_unavailable" when the listing count had to be asked for.</summary>
    public string Status { get; set; } = "ok";
    public string Error { get; set; } = "";

    /// <summary>The count every figure below was worked out against.</summary>
    public int ListingsPerMonth { get; set; }
    public int ActiveListings { get; set; }
    public bool ListingCountMeasured { get; set; }
    public bool UsingOverride { get; set; }

    public string CurrentPlanKey { get; set; } = "";
    public string CurrentPlanName { get; set; } = "";
    public string BillingCycle { get; set; } = "annual";

    public List<StorePlanOption> Options { get; set; } = [];

    public string BestPlanKey { get; set; } = "";
    public string BestPlanName { get; set; } = "";
    public decimal CurrentMonthlyCost { get; set; }
    public decimal BestMonthlyCost { get; set; }

    /// <summary>What switching plan is worth. Zero when the seller is already on the best one.</summary>
    public decimal MonthlySaving { get; set; }
    public decimal AnnualSaving { get; set; }

    /// <summary>Worth on top of the plan change, from paying for the same tier annually.</summary>
    public decimal BillingMonthlySaving { get; set; }
    public string BillingNote { get; set; } = "";

    /// <summary>Everything a switch is worth in a year — the plan change and the billing cycle.</summary>
    public decimal TotalAnnualSaving { get; set; }

    public bool AlreadyOnBestPlan { get; set; }

    /// <summary>The one sentence. An instruction when there is one, a confirmation when there is not.</summary>
    public string Headline { get; set; } = "";
    public string Detail { get; set; } = "";

    /// <summary>Where the next plan change lands, so the seller knows before they grow into it.</summary>
    public string NextStep { get; set; } = "";

    public decimal MonthlySales { get; set; }
    /// <summary>The store bill as a share of sales. Zero when there are no sales on record.</summary>
    public decimal CostShareOfSalesPercent { get; set; }

    public List<string> Honesty { get; set; } = [];
    public string RatesNote { get; set; } = "";
}

/// <summary>The seller's own two answers — which plan they are on, and how they pay for it.</summary>
public sealed class StorePlanSettings
{
    public string PlanKey { get; set; } = "none";
    public bool AnnualBilling { get; set; } = true;
    /// <summary>A listing count the seller typed. Null means "use what eBay reports".</summary>
    public int? ListingsOverride { get; set; }
}

/// <summary>The body of a settings save from the Store Plan screen.</summary>
public sealed class StorePlanSettingsRequest
{
    public string? PlanKey { get; set; }
    public bool? AnnualBilling { get; set; }
    public int? ListingsOverride { get; set; }
    /// <summary>The listing count already on screen, so saving a plan does not re-hit eBay.</summary>
    public int? ActiveListings { get; set; }
}
