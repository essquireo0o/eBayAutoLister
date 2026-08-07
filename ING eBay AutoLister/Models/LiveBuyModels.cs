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

    /// <summary>
    /// Both carried for the same reason as the rest, and this pair decides what the night actually
    /// cost: the marketplace collects sales tax on the hammer and the premium at checkout, so a row
    /// recorded without it reports a spend the seller's bank statement will disagree with — by about
    /// the size of the premium, on every lot. A ticked certificate is carried too, because "exempt"
    /// and "nobody said" are the same zero and different facts.
    /// </summary>
    public decimal? SalesTaxPercent { get; set; }
    public bool? TaxExempt { get; set; }

    /// <summary>
    /// Both carried for the same reason again, and this pair matters more than the rest: the card
    /// this lot was won off may have costed it at the show's extra-item rate because it rides in a
    /// box already going out (<see cref="Services.LiveShipShare"/>). A row recorded without them
    /// would put the full first-item rate back on and report the night as costing more than the
    /// seller's bank statement will — and it is the show name that lets the NEXT lot know this box
    /// exists at all.
    /// </summary>
    public decimal? AdditionalItemShipping { get; set; }
    public string? ShowName { get; set; }

    /// <summary>
    /// Carried for the same reason the rest are: a lot of three won at one hammer price is three
    /// units of stock and three units of resale, and a sheet that recorded it as one would report
    /// the night's best buy as its worst.
    /// </summary>
    public int? Quantity { get; set; }

    /// <summary>
    /// Carried for the same reason again: the card on screen had already cut its ceiling to what
    /// this condition actually fetches, and a row that valued the win at the mixed median would
    /// report a resale the card had spent its arithmetic refusing.
    /// </summary>
    public string? Condition { get; set; }

    /// <summary>The same question the card answers, asked at the winning bid. Everything the row
    /// shows comes back through <see cref="Services.LiveBidAdvisor.Build"/> from this.</summary>
    public LiveBidRequest AsBid() => new()
    {
        Title = Title,
        CurrentBid = WinningBid,
        ShippingCost = ShippingCost,
        AdditionalItemShipping = AdditionalItemShipping,
        ShowName = ShowName,
        BuyerFeePercent = BuyerFeePercent,
        SalesTaxPercent = SalesTaxPercent,
        TaxExempt = TaxExempt,
        TargetRoiPercent = TargetRoiPercent,
        Quantity = Quantity,
        Condition = Condition,
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

    /// <summary>
    /// How many things this row bought. One hammer price, <see cref="Units"/> objects to sell — so
    /// a lot of three won at $60 is three units of stock, and a sheet that counted it as one would
    /// tell the next card it is clear when it is not.
    /// </summary>
    /// <remarks>
    /// Rows written before this field existed carry 0; every reader takes <c>Math.Max(1, Units)</c>,
    /// which is what a row from an older sheet always meant.
    /// </remarks>
    public int Units { get; set; } = 1;

    /// <summary>
    /// Which show this lot came off, as the seller named it. Empty on rows written before this field
    /// existed and on any night the seller did not say — both of which simply mean the row cannot be
    /// matched to a box, so the next lot from that show is costed at full freight.
    /// </summary>
    /// <remarks>
    /// This is the field that makes a shipment a shipment. A live seller posts one box per show, so
    /// "what has already shipped from here tonight" is the question that decides what winning the
    /// next lot really adds — see <see cref="Services.LiveBuySheet.ShippingOnShow"/>.
    /// </remarks>
    public string ShowName { get; set; } = "";

    // ── What it cost ─────────────────────────────────────────────────────────
    public decimal WinningBid { get; set; }
    public decimal BuyerFeePercent { get; set; }
    public decimal BuyerFee { get; set; }

    /// <summary>
    /// What this lot was charged to get delivered — the MARGINAL cost, which on a lot riding along
    /// in a box already going out is the show's extra-item rate. That is what makes
    /// <see cref="BuySheet.Spent"/> the figure the seller's bank statement will agree with: charging
    /// six lots the full first-item rate would report a $17 shipping bill as $72.
    /// </summary>
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

    // ── What happened to it after the show ───────────────────────────────────
    // A won lot is money that has left, sitting in a box. It only comes back by being listed, so
    // the sheet remembers whether that has happened yet — and a row that has been listed says so
    // rather than offering to list it a second time and leaving two drafts of one item on disk.

    /// <summary>The local draft file this lot became. Empty until it has been listed.</summary>
    public string ListedDraftFile { get; set; } = "";

    /// <summary>The title the draft carries — the item name, cut to what eBay will accept.</summary>
    public string ListedTitle { get; set; } = "";

    /// <summary>What the draft asks. The comps' resale price, charm-rounded, never under what the
    /// lot cost. Null on a lot nothing could price: the draft is still made, with the price left
    /// for the seller.</summary>
    public decimal? ListedPrice { get; set; }

    /// <summary>The SKU minted for this lot, carried on the draft and on the deal card so the two
    /// are about the same object.</summary>
    public string ListedSku { get; set; } = "";

    public DateTime? ListedAtUtc { get; set; }

    /// <summary>The deal-board card carrying this lot's capital. 0 when the board refused it —
    /// the draft is the point, the card is the bookkeeping.</summary>
    public long DealId { get; set; }

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

    /// <summary>Rows that have become a draft listing. The rest are boxes in a hallway.</summary>
    public int ListedCount { get; set; }

    public DateTime? FirstWonUtc { get; set; }
    public DateTime? LastWonUtc { get; set; }

    /// <summary>The sheet in one sentence. Written on the server, next to the arithmetic.</summary>
    public string Say { get; set; } = "";
}

// ── Turning a won lot into a listing (see Services/WonLotListing.cs) ──────────────────────────
// A lot that is won and never listed is a loss with a receipt. The sheet knows what the item is,
// what it cost all in, and what the eBay sold comps say it resells for — which is every field the
// listing editor would otherwise ask the seller to type back in at midnight, from memory, having
// already closed the screen that knew them.

/// <summary>"List that one." Names one row on the buy sheet.</summary>
public sealed class LiveListRequest
{
    public string? Id { get; set; }
}

/// <summary>What listing a won lot did — the draft on disk, the card on the deal board, and the
/// sheet with the row now marked.</summary>
public sealed class LiveListResult
{
    public string Status { get; set; } = "ok";

    public string LotId { get; set; } = "";

    /// <summary>The draft's filename under the eBayListing folder, which is what opens it.</summary>
    public string DraftFile { get; set; } = "";

    public string Title { get; set; } = "";
    public string Sku { get; set; } = "";

    /// <summary>What the draft asks, or null when nothing priced the lot and the seller has to.</summary>
    public decimal? Price { get; set; }

    /// <summary>True when this row was already a draft. Nothing is written twice: pressing the
    /// button again opens the draft that exists rather than making a second one.</summary>
    public bool AlreadyListed { get; set; }

    /// <summary>The deal-board card this lot's capital went onto. 0 when the board refused it.</summary>
    public long DealId { get; set; }

    /// <summary>What was done, in one sentence, for the line above the card.</summary>
    public string Say { get; set; } = "";

    /// <summary>What the draft could not decide and the seller must — the condition, and the price
    /// on a lot nothing could price. Said out loud rather than left to be discovered on eBay.</summary>
    public List<string> Notes { get; set; } = [];

    /// <summary>The whole sheet, recomposed. The row's state moved, so handing back the row alone
    /// would leave the screen to work out the totals in JavaScript.</summary>
    public BuySheet Sheet { get; set; } = new();
}
