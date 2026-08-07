namespace ING_eBay_AutoLister.Models;

// ── What the room actually pays (see Services/LiveRoom.cs and Services/LiveRoomBook.cs) ────────
//
// Every read on the live card so far answers one question: what is the thing on screen WORTH. The
// comps say what it fetches, the sell-through says whether it moves, the gate says whether eBay
// will take it, the budget says whether the money is there. Then the app prints BID UP TO $240 and
// the seller sits through four minutes of bidding while somebody else pays $310 for it.
//
// Nothing in this app has ever measured the OTHER side of a live auction: the room. A ceiling is a
// statement about the item; whether the lot can be bought at that ceiling is a statement about who
// else is in the show — and it is the fact that decides whether the next two hours are worth
// spending. A room that clears at 60% of the app's ceilings is a room to be in. A room that clears
// above them is a room where every card on screen is a true ceiling for a lot the seller will never
// win, and the correct move is to leave.
//
// It is measurable, and it is measurable from evidence the seller is standing in the middle of: the
// hammer price of the lots they PRICED AND DID NOT WIN. Those are the most valuable data in the
// building and the app has been throwing every one of them away. One press records it.
//
// Nothing here is a sold comp and nothing here prices anything. A hammer price at a live show is
// what one bidder paid one seller in one room on one night — it is evidence about the ROOM, and
// letting it near the resale pipeline would be the app quoting an auction back to itself.

/// <summary>"It went for $X." A lot that was priced on this tab and went to somebody else.</summary>
/// <remarks>
/// The shape of <see cref="LiveWinRequest"/> and for the same reason: the row is written off the
/// SAME card, rebuilt by the SAME <see cref="Services.LiveBidAdvisor.Build"/> against the SAME held
/// comps, so the ceiling written on it is the ceiling the seller was actually looking at.
/// </remarks>
public sealed class LivePassRequest
{
    /// <summary>The handle on the comps this lot was priced against
    /// (<see cref="Services.LiveBidBoard"/>). Required: a hammer price recorded without the ceiling
    /// it is being measured against is a number with nothing to compare it to.</summary>
    public string? Token { get; set; }

    /// <summary>What was on screen, for the same guard the re-price uses — a token records the lot
    /// it was issued for and no other.</summary>
    public string? Title { get; set; }

    /// <summary>What it hammered at. Not the last bid the seller saw before looking away — the
    /// price it actually sold for, which is the only figure this whole file is about.</summary>
    public decimal? HammerPrice { get; set; }

    /// <summary>
    /// Which show this was. Required in practice: nothing is ever combined across an unnamed show,
    /// because "the room" is a fact about one host's audience and tonight's tab could be holding
    /// lots from three of them.
    /// </summary>
    public string? ShowName { get; set; }

    /// <summary>Carried so the ceiling written on the row is the card's own ceiling, costed at the
    /// card's own terms.</summary>
    public decimal? ShippingCost { get; set; }
    public decimal? AdditionalItemShipping { get; set; }
    public decimal? BuyerFeePercent { get; set; }
    public decimal? SalesTaxPercent { get; set; }
    public bool? TaxExempt { get; set; }
    public decimal? TargetRoiPercent { get; set; }
    public int? Quantity { get; set; }
    public string? Condition { get; set; }

    /// <summary>
    /// The card at the price it went for. Everything the row records comes back through
    /// <see cref="Services.LiveBidAdvisor.Build"/> from this.
    /// </summary>
    /// <remarks>
    /// <see cref="LiveBidRequest.NightBudget"/> is deliberately left behind, exactly as
    /// <see cref="LiveWinRequest.AsBid"/> leaves it behind and for the same reason: the ceiling on
    /// this row is what the room is being measured against, and that has to stay the MARKET's
    /// answer. A lot that hammered at twice the ceiling on a night the seller's cash had run out
    /// says nothing about the room, and recording it as a hot room would make this whole read a
    /// report on the seller's bank balance.
    /// </remarks>
    public LiveBidRequest AsBid() => new()
    {
        Title = Title,
        CurrentBid = HammerPrice,
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

/// <summary>Names one row of the room book — a mistyped hammer price, or a lot recorded twice.</summary>
public sealed class LiveRoomRowRequest
{
    public string? Id { get; set; }
    /// <summary>Which show to clear, when clearing rather than removing. Empty clears everything —
    /// the seller has moved on to a different night.</summary>
    public string? ShowName { get; set; }
}

/// <summary>One lot that was watched to the hammer and lost.</summary>
public sealed class PassedLot
{
    /// <summary>This row's own handle. Not the comp token — that expires in twenty minutes and this
    /// row is the whole point of the file.</summary>
    public string Id { get; set; } = "";

    public string Item { get; set; } = "";
    public string ShowName { get; set; } = "";
    public string CategoryLabel { get; set; } = "";
    public DateTime SeenAtUtc { get; set; }

    /// <summary>What it hammered at.</summary>
    public decimal HammerPrice { get; set; }

    /// <summary>
    /// What the app said to stop at, at the moment the lot was on screen. The market's ceiling: the
    /// budget is deliberately not carried into it — see <see cref="LivePassRequest.AsBid"/>. Zero
    /// when the card had no ceiling at all, and such a row is counted as watched and never rated.
    /// </summary>
    public decimal CeilingAtPass { get; set; }

    /// <summary>How many things the lot was, so a hammer price for three of them is never compared
    /// against a ceiling for one.</summary>
    public int Units { get; set; } = 1;

    /// <summary>bid | risky | stop | no_data — what the card said about it while it was on screen.</summary>
    public string Call { get; set; } = "";

    /// <summary>The comps behind the ceiling, for the row's own note. Never used to price anything.</summary>
    public int CompCount { get; set; }

    /// <summary>The row in a sentence — its accessible label, written on the server beside the
    /// arithmetic it describes.</summary>
    public string Say { get; set; } = "";
}

/// <summary>
/// One lot whose outcome is known: what it hammered at, and what the app said to stop at.
/// </summary>
/// <remarks>
/// Deliberately the same shape for a lot that was lost and a lot that was won. A room read built
/// only out of the lots the seller lost would be measuring the tail of its own distribution — a
/// seller wins the lots that go cheap, so leaving the wins out reports every room as hotter than
/// it is. <see cref="Won"/> is carried so the strip can say how the count splits, and for nothing
/// else: both kinds count identically toward what the room pays.
/// </remarks>
public readonly record struct LiveRoomLot(decimal Hammer, decimal Ceiling, bool Won);

/// <summary>
/// What is known about one show's room — every lot on it whose hammer price was recorded, won or
/// lost. Default is nothing, so a card built without a room book is priced and described exactly as
/// it was before this file existed.
/// </summary>
public readonly record struct LiveRoomTonight(IReadOnlyList<LiveRoomLot> Lots)
{
    public static readonly LiveRoomTonight Nothing = new([]);

    /// <summary>Never null, so callers can enumerate a <c>default</c> without checking.</summary>
    public IReadOnlyList<LiveRoomLot> Watched => Lots ?? [];
}

/// <summary>The five answers. See <see cref="Services.LiveRoom"/> for the bars between them.</summary>
public static class LiveRoomVerdicts
{
    /// <summary>The room clears well under the app's ceilings. There is arbitrage here to be had,
    /// and the ceiling on screen is not the number that decides the lot.</summary>
    public const string Cheap = "cheap";

    /// <summary>The room clears close to the app's ceilings. Winning means bidding to the ceiling
    /// and the margin is whatever the target return was.</summary>
    public const string Tight = "tight";

    /// <summary>The room clears ABOVE the app's ceilings. Every card here is a true ceiling for a
    /// lot that will not be bought at it, and the evening is the thing being spent.</summary>
    public const string Hot = "hot";

    /// <summary>Some lots watched, too few to call it a rate. Reported anyway — "two lots here went
    /// over your ceiling" is a true sentence — but never as a clearing rate.</summary>
    public const string Thin = "thin";

    /// <summary>No show named, or nothing recorded on it yet. The state every seller starts in.</summary>
    public const string Unread = "unread";
}

/// <summary>
/// What lots actually hammer for at this show, measured against what the app said to stop at.
/// See <see cref="Services.LiveRoom"/> for what it refuses to claim.
/// </summary>
/// <remarks>
/// Present on every card, including the ones with nothing recorded. A block that only appeared once
/// a room had been measured would be a block whose silence means both "this room is fine" and
/// "nobody has ever written a hammer price down" — and the second is the state the button exists
/// to change.
/// </remarks>
public sealed class LiveRoomRead
{
    /// <summary>True when enough lots were watched to state a clearing rate. False is not an error —
    /// it is the honest state of a show nobody has recorded three outcomes on yet.</summary>
    public bool Readable { get; set; }

    /// <summary>cheap | tight | hot | thin | unread. See <see cref="LiveRoomVerdicts"/>.</summary>
    public string Verdict { get; set; } = LiveRoomVerdicts.Unread;

    /// <summary>The show these lots were watched on, as the seller named it. Empty when unnamed,
    /// which is the commonest reason this read has nothing to say.</summary>
    public string ShowName { get; set; } = "";

    /// <summary>The strip's one line.</summary>
    public string Headline { get; set; } = "";

    /// <summary>The arithmetic behind it, said out loud.</summary>
    public string Note { get; set; } = "";

    /// <summary>The sentence for the card's warning list. Empty unless the room has been clearing
    /// above the app's ceilings — this block is silent on a room that can be bought in.</summary>
    public string Warning { get; set; } = "";

    // ── What was watched ─────────────────────────────────────────────────────
    /// <summary>Lots on this show whose hammer price is known.</summary>
    public int Watched { get; set; }

    /// <summary>How many of them the seller won. The rest went to the room.</summary>
    public int Won { get; set; }

    /// <summary>How many of them had a ceiling to be rated against. A lot the comps refused has a
    /// hammer price and no line to measure it by, so it is watched and not rated.</summary>
    public int Rated { get; set; }

    // ── What the room pays ───────────────────────────────────────────────────
    /// <summary>
    /// The middle of hammer ÷ ceiling across the rated lots. 0.62 means this room has been buying
    /// at 62% of what the app said to stop at. Zero when nothing was rated.
    /// </summary>
    /// <remarks>
    /// A median of the per-lot RATIOS, not the ratio of the two medians. One $900 lot among nine $20
    /// ones would otherwise decide the whole read.
    /// </remarks>
    public decimal ClearingRatio { get; set; }

    /// <summary>That, as a whole percent, for the strip.</summary>
    public int ClearingPercent { get; set; }

    /// <summary>How many of the rated lots hammered above the app's ceiling.</summary>
    public int OverCeiling { get; set; }

    // ── What it says about the lot on screen ─────────────────────────────────
    /// <summary>The ceiling this lot's card is carrying — the market's, not the wallet's.</summary>
    public decimal Ceiling { get; set; }

    /// <summary>
    /// Where this lot is likely to land: the ceiling above times the clearing ratio. Zero when there
    /// is no ceiling or no rate. An estimate off this room's own record and nothing else — it is not
    /// a price, and the card never bids to it.
    /// </summary>
    public decimal ExpectedHammer { get; set; }

    /// <summary>Ceiling − expected hammer. What the room has been leaving on the table, on this lot.
    /// Negative when the room has been clearing above the ceiling.</summary>
    public decimal RoomOverExpected { get; set; }
}

/// <summary>The room book as it stands, for the panel under the card.</summary>
public sealed class RoomBook
{
    public List<PassedLot> Lots { get; set; } = [];
    public int LotCount { get; set; }

    /// <summary>Shows with at least one row on them, newest first. The seller can be in three
    /// rooms a night and the totals must never cross between them.</summary>
    public List<RoomShow> Shows { get; set; } = [];

    /// <summary>The whole book in a sentence, written on the server beside the arithmetic.</summary>
    public string Say { get; set; } = "";
}

/// <summary>One show's line in the room book.</summary>
public sealed class RoomShow
{
    public string ShowName { get; set; } = "";
    public int Watched { get; set; }
    public int Rated { get; set; }
    public int OverCeiling { get; set; }
    public int ClearingPercent { get; set; }
    public string Verdict { get; set; } = LiveRoomVerdicts.Unread;
    public string Say { get; set; } = "";
    public DateTime LastSeenUtc { get; set; }
}
