namespace ING_eBay_AutoLister.Models;

/// <summary>
/// eBay Auto-Buy: rules that watch eBay and, when armed, buy on their own. This is the one feature
/// in the app that spends real money without a person in the loop, so the model is built around the
/// brakes, not the accelerator.
/// </summary>
/// <remarks>
/// <para>Three switches must all be on before a single dollar moves, and every one of them ships
/// off:</para>
/// <list type="number">
///   <item>the master arm (<see cref="AutoBuySettings.Armed"/>),</item>
///   <item>live buying (<see cref="AutoBuySettings.LiveBuying"/>) — off means every match is
///     written down as it would have been bought, and nothing is placed,</item>
///   <item>the individual rule's own <see cref="AutoBuyRule.Enabled"/> flag.</item>
/// </list>
/// <para>On top of that, no rule can exist without a per-item price ceiling and a total budget, and
/// there is a global budget and a global per-day count that sit above all rules. A rule can never
/// buy the same eBay item twice — every item it acts on is remembered — and a purchase that fails
/// is recorded and does not silently retry.</para>
/// </remarks>
public enum AutoBuyMode
{
    /// <summary>Buy a fixed-price listing outright when its price is at or below the ceiling.</summary>
    BuyItNow,
    /// <summary>Send a Best Offer at the offer price on a fixed-price listing that takes offers.</summary>
    BestOffer,
    /// <summary>Place a maximum bid on an ending-soon auction whose current price is under the ceiling.</summary>
    AuctionBid,
}

/// <summary>What one attempt to act on one listing came to. Every row is one decision, kept.</summary>
public enum AutoBuyOutcome
{
    /// <summary>Live buying was off (or the rule/master was disarmed): recorded, not placed.</summary>
    Simulated,
    /// <summary>eBay accepted the purchase, offer, or bid.</summary>
    Placed,
    /// <summary>eBay refused it, or the call failed. The reason is on the row.</summary>
    Failed,
    /// <summary>A cap stopped it — the rule's budget or count, or a global limit — before any spend.</summary>
    Skipped,
}

/// <summary>One saved auto-buy rule.</summary>
public sealed class AutoBuyRule
{
    public long Id { get; set; }

    /// <summary>What the card calls it. Defaults to the search when left blank.</summary>
    public string Name { get; set; } = "";

    /// <summary>The eBay search this rule buys out of.</summary>
    public string Query { get; set; } = "";

    public AutoBuyMode Mode { get; set; } = AutoBuyMode.BuyItNow;

    /// <summary>NEW, USED, REFURBISHED, FOR_PARTS, or empty for any. Passed to the eBay search.</summary>
    public string Condition { get; set; } = "";

    /// <summary>
    /// The most this rule will ever pay for one item, shipping excluded. A listing priced above this
    /// is never acted on. Required and above zero — a rule with no ceiling cannot be saved.
    /// </summary>
    public decimal MaxItemPrice { get; set; }

    /// <summary>
    /// For <see cref="AutoBuyMode.BestOffer"/>, the amount to offer; for
    /// <see cref="AutoBuyMode.AuctionBid"/>, the maximum bid. Ignored for Buy It Now. Never allowed
    /// above <see cref="MaxItemPrice"/>.
    /// </summary>
    public decimal OfferOrBidPrice { get; set; }

    /// <summary>Skip any listing whose title contains one of these words. Comma or newline separated.</summary>
    public string ExcludeKeywords { get; set; } = "";

    /// <summary>Only act on sellers at or above this feedback score. Zero means no floor.</summary>
    public int MinSellerFeedback { get; set; }

    /// <summary>The total this rule may ever spend. Once <see cref="Spent"/> reaches it, the rule rests.</summary>
    public decimal BudgetCap { get; set; }

    /// <summary>The most items this rule may ever buy. Zero means no count limit (budget still binds).</summary>
    public int MaxBuys { get; set; }

    /// <summary>What this rule has committed so far, live buys only. Simulations do not spend.</summary>
    public decimal Spent { get; set; }

    /// <summary>How many live buys this rule has made.</summary>
    public int Buys { get; set; }

    /// <summary>The rule's own on/off. Off, and it is not scanned at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>Minutes between scans of this rule.</summary>
    public int IntervalMinutes { get; set; } = 15;

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastRunUtc { get; set; }
    public DateTimeOffset? NextRunUtc { get; set; }
    public string LastStatus { get; set; } = "";

    /// <summary>True once this rule can spend no more — its budget or its count is used up.</summary>
    public bool IsExhausted =>
        Spent >= BudgetCap || (MaxBuys > 0 && Buys >= MaxBuys);

    /// <summary>The most a single further buy may cost without breaching this rule's remaining budget.</summary>
    public decimal RemainingBudget => Math.Max(0m, BudgetCap - Spent);
}

/// <summary>
/// A partial edit to a rule. Absent fields are left as they were, so the pause toggle can post
/// <c>{ id, enabled }</c> without blanking the ceilings the seller set.
/// </summary>
public sealed class AutoBuyRuleRequest
{
    public long? Id { get; set; }
    public string? Name { get; set; }
    public string? Query { get; set; }
    public string? Mode { get; set; }
    public string? Condition { get; set; }
    public decimal? MaxItemPrice { get; set; }
    public decimal? OfferOrBidPrice { get; set; }
    public string? ExcludeKeywords { get; set; }
    public int? MinSellerFeedback { get; set; }
    public decimal? BudgetCap { get; set; }
    public int? MaxBuys { get; set; }
    public int? IntervalMinutes { get; set; }
    public bool? Enabled { get; set; }
}

/// <summary>One row in the ledger: what a rule did, or would have done, to one listing.</summary>
public sealed class AutoBuyExecution
{
    public long Id { get; set; }
    public long RuleId { get; set; }
    public string RuleName { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public AutoBuyMode Mode { get; set; }
    public decimal Price { get; set; }
    public AutoBuyOutcome Outcome { get; set; }
    public string Detail { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>The three master switches and the limits that sit above every rule. One row, ever.</summary>
public sealed class AutoBuySettings
{
    /// <summary>The master arm. Off, and nothing scans and nothing buys. Ships off.</summary>
    public bool Armed { get; set; }

    /// <summary>
    /// Off means simulate: matches are recorded as "would have bought" and no purchase is placed.
    /// On means real money. Ships off, and turning it on is a separate, deliberate act from arming.
    /// </summary>
    public bool LiveBuying { get; set; }

    /// <summary>The ceiling on everything the feature may spend, across all rules, ever.</summary>
    public decimal GlobalBudgetCap { get; set; } = 500m;

    /// <summary>What every rule together has spent on live buys.</summary>
    public decimal GlobalSpent { get; set; }

    /// <summary>The most live buys the feature may make in one calendar day (UTC), across all rules.</summary>
    public int MaxBuysPerDay { get; set; } = 5;

    /// <summary>How many live buys have happened today (UTC). Reset by the store when the day rolls.</summary>
    public int BuysToday { get; set; }

    public string BuysTodayDateUtc { get; set; } = "";

    public decimal GlobalRemainingBudget => Math.Max(0m, GlobalBudgetCap - GlobalSpent);
}

/// <summary>The whole feature in one shape, for the console: settings, rules, recent ledger, scan state.</summary>
public sealed record AutoBuyStatus(
    AutoBuySettings Settings,
    IReadOnlyList<AutoBuyRule> Rules,
    IReadOnlyList<AutoBuyExecution> Recent,
    bool Scanning,
    long? ScanningRuleId,
    DateTimeOffset? LastScanUtc,
    string SafetyLine);
