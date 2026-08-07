namespace ING_eBay_AutoLister.Models;

// ── The show's buy sheet (see Services/LiveBuySheet.cs) ───────────────────────────────────────
// The arbitrage card ends at the hammer. It says BID UP TO $240, the seller wins at $180, and then
// the app has nothing more to say — the one number that decides whether the night made money (what
// was spent against what it is all worth) exists only in the seller's head and in a stack of
// Whatnot receipts they will reconcile a week later, if ever.
//
// This is the other half of the same decision. A won lot is not a new calculation: it is the SAME
// card, built by the SAME LiveBidAdvisor.Build against the SAME held comps, at the price it
// actually hammered at. So a row on this sheet cannot disagree with the card the bid was made on —
// there is one function in this app that turns sold comps into money, and this is not a second one.

/// <summary>"I won that one." The lot on screen, at the price it hammered at.</summary>
public sealed class LiveWinRequest
{
    /// <summary>The handle on the comps this lot was priced against
    /// (<see cref="Services.LiveBidBoard"/>). Required: a win recorded without the sold history
    /// behind it would be a spend with an invented resale price beside it.</summary>
    public string? Token { get; set; }

    /// <summary>What was won, for the same guard the re-price uses — a token prices the lot it was
    /// issued for and no other.</summary>
    public string? Title { get; set; }

    /// <summary>What it hammered at. Not the ceiling, not the last bid shown — what was paid.</summary>
    public decimal? WinningBid { get; set; }

    /// <summary>Carried from the card so the row's landed cost is the card's landed cost.</summary>
    public decimal? ShippingCost { get; set; }
    public decimal? BuyerFeePercent { get; set; }
    public decimal? TargetRoiPercent { get; set; }

    /// <summary>The same question the card answers, asked at the winning bid. Everything the row
    /// shows comes back through <see cref="Services.LiveBidAdvisor.Build"/> from this.</summary>
    public LiveBidRequest AsBid() => new()
    {
        Title = Title,
        CurrentBid = WinningBid,
        ShippingCost = ShippingCost,
        BuyerFeePercent = BuyerFeePercent,
        TargetRoiPercent = TargetRoiPercent,
        Token = Token,
    };
}

/// <summary>Names one row on the sheet — the lot that was recorded by mistake, or lost.</summary>
public sealed class LiveSheetRowRequest
{
    public string? Id { get; set; }
}

/// <summary>One lot that was won, costed at what it actually cost.</summary>
public sealed class WonLot
{
    /// <summary>This row's own handle. Not the comp token — that expires in twenty minutes and this
    /// row outlives the show.</summary>
    public string Id { get; set; } = "";

    public string Item { get; set; } = "";
    public string CategoryLabel { get; set; } = "";
    public DateTime WonAtUtc { get; set; }

    // ── What it cost ─────────────────────────────────────────────────────────
    public decimal WinningBid { get; set; }
    public decimal BuyerFeePercent { get; set; }
    public decimal BuyerFee { get; set; }
    public decimal ShippingCost { get; set; }
    /// <summary>Bid + premium + shipping. The number that has to be earned back before any of this
    /// was worth doing.</summary>
    public decimal LandedCost { get; set; }

    // ── What it is worth ─────────────────────────────────────────────────────
    /// <summary>
    /// False when the lot was recorded off a card that could not price it. The spend is still real
    /// and still counts — the resale side is simply absent, and the sheet says so rather than
    /// carrying a zero that would drag every total down as though the thing were worthless.
    /// </summary>
    public bool Priced { get; set; }

    /// <summary>What the eBay sold comps say it resells for. Also what to list it at.</summary>
    public decimal? ResalePrice { get; set; }
    /// <summary>Net, after eBay's cut and the outbound shipping — the card's own profit at this
    /// exact bid, not a margin taken off the resale price.</summary>
    public decimal? ProjectedProfit { get; set; }
    public decimal? ProjectedRoiPercent { get; set; }
    public int? DaysToCash { get; set; }

    // ── What the app had said before the hammer ──────────────────────────────
    /// <summary>The call at the winning bid: bid | risky | stop | no_data. A lot bought past its own
    /// ceiling comes back from <c>Build</c> as a stop, so this is not a second judgement.</summary>
    public string Call { get; set; } = LiveBidCalls.NoData;

    /// <summary>The ceiling this lot had when it was won. Kept because it is the only number that
    /// stops being available the moment the comps are let go, and it is the one the seller needs
    /// tomorrow to know whether tonight's discipline held.</summary>
    public decimal CeilingAtWin { get; set; }

    /// <summary>How far past that ceiling the winning bid went. Zero when it held.</summary>
    public decimal PaidOverCeiling { get; set; }

    public int CompCount { get; set; }
    public decimal? SellThroughRate { get; set; }
    public string EvidenceTier { get; set; } = "";

    /// <summary>The row in one sentence, written beside the money for the same reason
    /// <see cref="LiveBidCard.Say"/> is. It is the row's accessible label.</summary>
    public string Say { get; set; } = "";
}

/// <summary>Every lot won since the sheet was last cleared, and what they add up to.</summary>
public sealed class BuySheet
{
    public string Status { get; set; } = "ok";

    /// <summary>Newest first — the lot just won is the one being looked for.</summary>
    public List<WonLot> Lots { get; set; } = [];

    public int LotCount { get; set; }

    /// <summary>All-in, across every row including the ones nothing could price. This is the
    /// number the bank statement will agree with.</summary>
    public decimal Spent { get; set; }

    /// <summary>The spend behind the priced rows alone — the denominator the return below is a
    /// return on. Mixing an unpriced lot's cost into that would report a lower return than the
    /// evidence supports and call it caution.</summary>
    public decimal PricedSpend { get; set; }

    public decimal ProjectedResale { get; set; }
    public decimal ProjectedProfit { get; set; }
    public decimal? ProjectedRoiPercent { get; set; }

    /// <summary>Rows won above their own ceiling, and by how much in total. The discipline half of
    /// the sheet: a night can be profitable in aggregate and still be losing money on the two lots
    /// the seller got carried away on.</summary>
    public int OverpaidCount { get; set; }
    public decimal OverpaidBy { get; set; }

    /// <summary>Rows whose projected profit is not positive — bought at a loss, on the app's own
    /// numbers.</summary>
    public int LosingCount { get; set; }

    /// <summary>Rows with no resale side at all.</summary>
    public int UnpricedCount { get; set; }

    public DateTime? FirstWonUtc { get; set; }
    public DateTime? LastWonUtc { get; set; }

    /// <summary>The sheet in one sentence. Written on the server, next to the arithmetic.</summary>
    public string Say { get; set; } = "";
}
