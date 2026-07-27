namespace ING_eBay_AutoLister.Models;

// ── Snap & Source ─────────────────────────────────────────────────────────────
// Every other sourcing screen in this app answers "what should I go and buy?" — a scan, a board,
// a ranking, minutes of work at a desk. This one answers the only question that gets asked
// standing up, holding the thing, with somebody waiting: BUY or PASS.
//
// It is the same pricing pipeline underneath (AnalyzeProductAsync → ResalePricing →
// LocalArbitrageAnalyzer.Build), deliberately: a verdict that disagreed with the Local Deals board
// about the same item would mean the app has two opinions and the seller has none. What is
// different is the input (a pasted URL or a photo, not a saved search) and the output (one word
// and one sentence, not a table).

/// <summary>One Buy/Pass question. Exactly one of <see cref="Url"/>, <see cref="ImageBase64"/> or
/// <see cref="Title"/> identifies the item; <see cref="AskPrice"/> is what they want for it.</summary>
public sealed class SnapRequest
{
    /// <summary>A listing URL — Craigslist, Facebook, OfferUp, eBay, a retailer's product page.</summary>
    public string? Url { get; set; }

    /// <summary>A photo of the thing itself, base64, no data: prefix. Read by Claude vision.</summary>
    public string? ImageBase64 { get; set; }
    public string? MimeType { get; set; }

    /// <summary>What it is, typed — or the identification a previous snap produced, re-priced
    /// after the seller corrected it or found out the price.</summary>
    public string? Title { get; set; }

    /// <summary>What the seller wants for it. Null means nobody has said yet, which at a yard sale
    /// is the normal case — the answer then is a price to pay rather than a verdict on one.</summary>
    public decimal? AskPrice { get; set; }

    /// <summary>Optional category override, same ids as the Local Deals board.</summary>
    public string? CategoryId { get; set; }
}

/// <summary>What the model saw in the photo. Only the fields a price lookup and a walk-away
/// decision actually need — this is a field tool, not the listing writer.</summary>
public sealed class SnapIdentity
{
    /// <summary>The eBay-search-shaped name: brand + model + the spec that distinguishes it.
    /// This is what the comp lookup runs against, so it is the one field that has to be right.</summary>
    public string Title { get; set; } = "";
    public string Brand { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>NEW / LIKE_NEW / USED_GOOD / … — the same vocabulary the listing writer uses.</summary>
    public string Condition { get; set; } = "";
    /// <summary>The visible wear, in a phrase. Empty when there is nothing to say.</summary>
    public string ConditionNote { get; set; } = "";
    /// <summary>high | medium | low — how sure the model is that it named the right product. Low
    /// means the price below is for whatever the model guessed, and the seller should read the
    /// name before trusting the number.</summary>
    public string Certainty { get; set; } = "low";
    /// <summary>The one thing to physically check before handing over money — the thing a photo
    /// cannot answer. "Power it on", "check for the charger", "look for a cracked heel".</summary>
    public string CheckThis { get; set; } = "";
}

/// <summary>The answer. One word, one sentence, and the numbers behind them.</summary>
public sealed class SnapResult
{
    public string Status { get; set; } = "ok";      // ok | error
    public string Error { get; set; } = "";

    // ── The verdict ──────────────────────────────────────────────────────────
    /// <summary>buy | close | pass | buy_under | unknown. See <see cref="Services.SnapJudge"/>.</summary>
    public string Call { get; set; } = SnapCalls.Unknown;
    /// <summary>The word on the big badge: BUY, PASS, CLOSE CALL, BUY UNDER $40, CAN'T PRICE IT.</summary>
    public string CallLabel { get; set; } = "";
    /// <summary>The one line under it, in this item's own numbers. Never generic.</summary>
    public string Reason { get; set; } = "";

    // ── What was priced ──────────────────────────────────────────────────────
    public string Item { get; set; } = "";
    /// <summary>The title the comp lookup actually ran against, when it differs from Item.</summary>
    public string PricedAs { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    /// <summary>Craigslist / Facebook Marketplace / eBay / Photo / Typed.</summary>
    public string SourceLabel { get; set; } = "";
    /// <summary>url | photo | text — which of the three ways in was used.</summary>
    public string InputMode { get; set; } = "";
    public string CategoryLabel { get; set; } = "";

    // ── The money ────────────────────────────────────────────────────────────
    public decimal? AskPrice { get; set; }
    /// <summary>False when nobody has named a price yet. Everything measured against the ask —
    /// profit, ROI — is null in that case rather than computed against a zero the seller never
    /// agreed to, and the verdict becomes a price to pay instead of a judgement on one.</summary>
    public bool AskWasKnown { get; set; }
    public decimal? ResalePrice { get; set; }
    public decimal? QuickSalePrice { get; set; }
    public decimal? NetProfit { get; set; }
    public decimal? RoiPercent { get; set; }
    public decimal? Fees { get; set; }
    public decimal? ShipCost { get; set; }

    /// <summary>The highest price that still breaks even. Pay more than this and the flip loses
    /// money — it is the walk-away number, not a target.</summary>
    public decimal? BuyMax { get; set; }
    /// <summary>The highest price that still clears the app's own "worth doing" bar
    /// (<see cref="Services.LocalArbitrageAnalyzer.SolidRoiPercent"/> /
    /// <see cref="Services.LocalArbitrageAnalyzer.SolidProfit"/>). The number to actually offer.</summary>
    public decimal? PayUpTo { get; set; }
    public decimal? ProfitAtPayUpTo { get; set; }

    // ── How much to believe it ───────────────────────────────────────────────
    public int CompCount { get; set; }
    public int ConfidenceScore { get; set; }
    public string ConfidenceLevel { get; set; } = "";
    /// <summary>confident | low | none — graded by LocalArbitrageAnalyzer.GradeEvidence, the same
    /// grading every other board in the app dims its percentages on.</summary>
    public string EvidenceTier { get; set; } = LocalArbitrageEvidence.None;
    public string EvidenceNote { get; set; } = "";
    public bool IdentityVerified { get; set; } = true;

    // ── How long the money is tied up ────────────────────────────────────────
    public int? DaysToSell { get; set; }
    public int? DaysToCash { get; set; }
    public string SpeedLabel { get; set; } = "";

    /// <summary>Set only when a photo was read. Carries the model's certainty and the one thing to
    /// check by hand — both of which matter more than any number when the ID came from a picture.</summary>
    public SnapIdentity? Identity { get; set; }

    /// <summary>eBay's own sold-and-completed search for this item. The seller's own eyes are the
    /// last check, and on a low-evidence row they are the only one worth having.</summary>
    public string SoldSearchUrl { get; set; } = "";

    public List<string> Warnings { get; set; } = [];
    public long ElapsedMs { get; set; }
}

/// <summary>The five answers. Spelled once so the badge, the tests and the CSS agree.</summary>
public static class SnapCalls
{
    /// <summary>Priced, and worth buying at the price being asked.</summary>
    public const string Buy = "buy";
    /// <summary>Profitable but marginal, or profitable on evidence too thin to bank on.</summary>
    public const string Close = "close";
    /// <summary>Loses money at that price, or is not worth the handling at any price.</summary>
    public const string Pass = "pass";
    /// <summary>Nobody has named a price. There is real headroom — here is the number to stop at.</summary>
    public const string BuyUnder = "buy_under";
    /// <summary>No sold history matched, or the category refused to be valued from a title.</summary>
    public const string Unknown = "unknown";
}

/// <summary>What the page said about itself — title, price, picture. Pure parse output, no I/O.</summary>
public sealed class SnapPageFacts
{
    public string Title { get; set; } = "";
    public decimal? Price { get; set; }
    public string PriceText { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string SiteLabel { get; set; } = "";
    /// <summary>True when the page advertises the item as free rather than priced at nothing.</summary>
    public bool IsFree { get; set; }

    public bool HasTitle => Title.Length > 0;
}
