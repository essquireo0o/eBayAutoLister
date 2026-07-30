namespace ING_eBay_AutoLister.Models;

// The Tax Pack. Money Made answers "what did I make?"; this answers the question that decides how
// much of it the seller keeps — "what do I owe on it, and what am I about to overpay?"
//
// It is a different number from the one on Money Made, on purpose, and the difference is the whole
// point of the feature:
//
//   * A sale with no recorded cost contributes NOTHING to Money Made. To the IRS it is taxable in
//     full, as though the goods were free. So the figure that is honestly conservative on the
//     earnings screen is the one that costs real money on a return, and this is where that gets
//     said out loud with a dollar figure attached.
//   * The seller's own labour is charged against profit everywhere else in this app. It is not a
//     deductible expense on a sole proprietor's Schedule C — you cannot pay yourself a wage and
//     deduct it — so it is added back here.
//
// Nothing in here is tax advice, nothing is filed, and no figure is rounded in the seller's favour.

/// <summary>One line of Schedule C, with the arithmetic that produced it.</summary>
/// <remarks>
/// <see cref="Basis"/> exists because a number an accountant cannot trace is a number they will
/// re-derive by hand, which is the cost this feature is supposed to remove.
/// </remarks>
public sealed class TaxLine
{
    /// <summary>The Schedule C line number — "1", "4", "10", "27a", "31". Not always an integer.</summary>
    public string Line { get; set; } = "";
    public string Label { get; set; } = "";
    public decimal Amount { get; set; }

    /// <summary>Plain English: what was added up to get here.</summary>
    public string Basis { get; set; } = "";

    /// <summary>True when every dollar is a figure eBay reported or the seller typed, with nothing estimated.</summary>
    public bool Measured { get; set; } = true;

    /// <summary>Subtotals (lines 3, 5, 7, 28, 31) are rendered heavier than the components above them.</summary>
    public bool IsSubtotal { get; set; }
}

/// <summary>One estimated-tax period, with the date the payment is actually due.</summary>
/// <remarks>
/// The four periods are NOT calendar quarters — the IRS windows are 3, 2, 3 and 4 months long.
/// Splitting a year into four equal quarters puts June's sales in the wrong payment and is the
/// most common way a seller ends up with an underpayment penalty on a year they had the money for.
/// </remarks>
public sealed class TaxQuarter
{
    public string Name { get; set; } = "";
    /// <summary>"January 1 – March 31" — the sales window, not the payment date.</summary>
    public string Covers { get; set; } = "";
    public DateTimeOffset DueDate { get; set; }

    public int Sales { get; set; }
    public decimal NetProfit { get; set; }
    /// <summary>What to have set aside for this period, at the seller's chosen rate.</summary>
    public decimal SetAside { get; set; }

    /// <summary>The due date has passed.</summary>
    public bool IsPast { get; set; }
    /// <summary>Today falls inside this period's sales window — the one being earned into now.</summary>
    public bool IsCurrent { get; set; }
}

/// <summary>
/// The money at stake: proceeds that will be taxed as pure profit because nothing records what the
/// goods cost.
/// </summary>
public sealed class TaxCostGap
{
    public int Sales { get; set; }
    /// <summary>Proceeds after fees and postage on those sales — what gets taxed with no cost against it.</summary>
    public decimal TaxableProceeds { get; set; }
    /// <summary>Tax on that, at the seller's combined rate. The number this feature exists to show.</summary>
    public decimal TaxAtRisk { get; set; }
}

/// <summary>What eBay will report to the IRS, and why it will not match anything on this page.</summary>
public sealed class Form1099KCheck
{
    /// <summary>Gross payments on eBay-sourced sales — sale price plus buyer-paid shipping, before refunds.</summary>
    public decimal ExpectedGross { get; set; }
    public int Sales { get; set; }
    /// <summary>Refunds eBay does NOT subtract from the form, which the seller deducts themselves.</summary>
    public decimal RefundsNotDeducted { get; set; }
    public List<string> Notes { get; set; } = [];
}

/// <summary>The rates the whole pack is computed at. Editable, because only the seller knows them.</summary>
public sealed class TaxAssumptions
{
    /// <summary>The seller's marginal federal income tax bracket, in percent.</summary>
    public decimal IncomeTaxRatePercent { get; set; } = 12m;
    /// <summary>Self-employment tax rate — 15.3% (12.4% Social Security + 2.9% Medicare).</summary>
    public decimal SelfEmploymentRatePercent { get; set; } = 15.3m;
    /// <summary>True when net profit cleared the $400 floor that makes SE tax apply at all.</summary>
    public bool SelfEmploymentApplies { get; set; }
    /// <summary>What one more dollar of provable expense is worth off the tax bill, in percent.</summary>
    public decimal DeductionValuePercent { get; set; }
}

/// <summary>The seller changing the one assumption they alone can supply.</summary>
public sealed class TaxRateRequest
{
    public decimal? IncomeTaxRatePercent { get; set; }
}

/// <summary>Everything the Tax Pack renders, for one year.</summary>
public sealed class TaxPackResult
{
    public int Year { get; set; }
    public bool HasSales { get; set; }
    /// <summary>True when the year is still running, so the figures are a year-to-date position.</summary>
    public bool YearInProgress { get; set; }

    public List<TaxLine> ScheduleC { get; set; } = [];

    /// <summary>Schedule C line 31 — the figure the tax is computed from.</summary>
    public decimal NetProfit { get; set; }
    public decimal SelfEmploymentTax { get; set; }
    public decimal IncomeTax { get; set; }
    public decimal TotalTax { get; set; }
    /// <summary>Total tax as a share of net profit — what to hold back out of every dollar of profit.</summary>
    public decimal EffectiveRatePercent { get; set; }

    /// <summary>Every expense this pack can substantiate, and what claiming them saves.</summary>
    public decimal DeductionsTotal { get; set; }
    public decimal TaxSavedByDeductions { get; set; }

    public TaxCostGap CostGap { get; set; } = new();
    public Form1099KCheck Form1099K { get; set; } = new();
    public TaxAssumptions Assumptions { get; set; } = new();
    public List<TaxQuarter> Quarters { get; set; } = [];

    /// <summary>
    /// Net profit as the Money Made screen reports it for the same year, so the two can be told
    /// apart on purpose rather than looking like a bug.
    /// </summary>
    public decimal EarningsNetProfit { get; set; }

    /// <summary>Plain-language statements of what these totals include, exclude and assume.</summary>
    public List<string> Honesty { get; set; } = [];

    /// <summary>Years the seller actually has sales in — what the year picker offers.</summary>
    public List<int> AvailableYears { get; set; } = [];

    /// <summary>The summary an accountant gets, and the sale-by-sale backup behind it.</summary>
    public string SummaryCsv { get; set; } = "";
    public string SummaryCsvFilename { get; set; } = "";
    public string LedgerCsv { get; set; } = "";
    public string LedgerCsvFilename { get; set; } = "";
}
