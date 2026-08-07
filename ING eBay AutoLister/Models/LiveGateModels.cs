namespace ING_eBay_AutoLister.Models;

// ── Whether eBay will let you sell it at all (see Services/LiveResaleGate.cs) ─────────────────
//
// Every figure on the live card is the price of an eBay listing. The resale price is what one
// fetched on eBay, the sell-through is the share of eBay listings that sold, the break-even is
// after eBay's cut, and the ceiling is the bid that keeps all of that clearing a target return.
//
// Eighteen sessions of this card have computed that price beautifully and not one of them has
// asked whether the listing is allowed to exist. On a live-selling feed that is not a theoretical
// gap: replica handbags, loose ammunition, swatched makeup, vape kits and sealed bottles of
// bourbon all go across those screens nightly, and every one of them prices like a spectacular
// flip against genuine sold comps. A ceiling of $340 on a bag that eBay will delete the listing
// for is not a slightly wrong number — it is the whole card pointed at a loss with no undo.
//
// And above the price there is a softer gate the card has also never mentioned: eBay's Authenticity
// Guarantee. Over a threshold, sneakers, handbags, watches, jewellery, graded cards and streetwear
// do not go from the seller to the buyer at all. They go to an eBay authentication hub first, which
// means the money lands days later than the card's "days to cash" says — and if the thing turns out
// to be fake, the sale is refunded and the seller is holding it.
//
// So this reads the name for both, before the arbitrage means anything.

/// <summary>The five answers. See <see cref="Services.LiveResaleGate"/> for the rules behind them.</summary>
public static class LiveGateVerdicts
{
    /// <summary>Nothing in the name matched a rule. The ordinary lot, and most of them.</summary>
    public const string Clear = "clear";

    /// <summary>An Authenticity Guarantee category. It can be listed, and it goes through eBay's
    /// authenticator on its way to the buyer — later money, and a fake is a refund.</summary>
    public const string Authenticated = "authenticated";

    /// <summary>eBay allows it under conditions the seller has to meet themselves. Said, never
    /// priced: the app cannot know whether this seller holds the licence.</summary>
    public const string Restricted = "restricted";

    /// <summary>eBay does not allow it. The only read on this card that can override the call —
    /// see <see cref="Services.LiveResaleGate"/> for why that is a correction and not a haircut.</summary>
    public const string Blocked = "blocked";

    /// <summary>Nothing to read: no name.</summary>
    public const string Unreadable = "unreadable";
}

/// <summary>
/// What eBay's own selling policies say about the thing on screen, read off its name.
/// See <see cref="Services.LiveResaleGate"/> for the catalogue and for what it refuses to claim.
/// </summary>
public sealed class LiveGateRead
{
    /// <summary>True once there was a name to read. False is not an error — it is the honest state
    /// of a card with nothing typed on it.</summary>
    public bool Readable { get; set; }

    /// <summary>clear | authenticated | restricted | blocked | unreadable.
    /// See <see cref="LiveGateVerdicts"/>.</summary>
    public string Verdict { get; set; } = LiveGateVerdicts.Unreadable;

    /// <summary>The strip's one line.</summary>
    public string Headline { get; set; } = "";

    /// <summary>What eBay's rule actually is, said out loud, so the seller can disagree with it.</summary>
    public string Note { get; set; } = "";

    /// <summary>The strip's badge — CAN'T LIST, CHECK FIRST, +4 DAYS TO CASH. Empty on a clear lot,
    /// which is the only state with nothing to flag. Written here rather than mapped from the
    /// verdict in the browser, so there is one place a state's words are decided.</summary>
    public string Tag { get; set; } = "";

    /// <summary>The sentence for the card's warning list. Empty on a clear lot — this block is
    /// silent about a lot with nothing wrong with it.</summary>
    public string Warning { get; set; } = "";

    /// <summary>The badge's line, and only on <see cref="LiveGateVerdicts.Blocked"/>. Empty
    /// everywhere else, because nothing else here is allowed near the call.</summary>
    public string Reason { get; set; } = "";

    // ── Which rule fired ─────────────────────────────────────────────────────
    /// <summary>The rule's name — "Replicas and counterfeits", "Sneakers". Empty on a clear lot.</summary>
    public string RuleName { get; set; } = "";

    /// <summary>The words in the lot's own name that fired it. The seller has to be able to see
    /// WHY, in seconds, because a wrong match is fixed by retyping the name.</summary>
    public string Matched { get; set; } = "";

    /// <summary>eBay's policy in one sentence, as the app understands it.</summary>
    public string Policy { get; set; } = "";

    // ── The authentication threshold ─────────────────────────────────────────
    /// <summary>The sale price at or above which eBay's authenticator gets involved. Zero on every
    /// rule that has no threshold, which is every blocked and restricted one.</summary>
    public decimal ThresholdPrice { get; set; }

    /// <summary>What one unit was priced at when the threshold was checked. Zero when nothing
    /// priced it.</summary>
    public decimal PricedAt { get; set; }

    /// <summary>
    /// True when the card's own per-unit resale price is at or above <see cref="ThresholdPrice"/> —
    /// so this really does route through the hub. False on an authenticated verdict means the
    /// category matched and <b>nothing priced it</b>: the threshold could not be checked, which is
    /// worth saying and is not worth claiming.
    /// </summary>
    public bool OverThreshold { get; set; }

    /// <summary>Roughly how many extra days the authentication leg adds before the money lands.
    /// A stated assumption, not a measurement — see
    /// <see cref="Services.LiveResaleGate.AuthenticationDays"/>.</summary>
    public int ExtraDaysToCash { get; set; }

    /// <summary>True when this lot cannot be listed on eBay at all, so no price on the card above
    /// it is a price of anything. The one flag here the call reads.</summary>
    public bool Stops => Verdict == LiveGateVerdicts.Blocked;
}
