using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The won lot, turned into the listing that gets the money back.
/// </summary>
/// <remarks>
/// <para>
/// The buy sheet ends at "you spent $1,240 tonight and it is worth $3,100". Every dollar of that
/// second figure arrives only if the boxes get listed, and the gap between winning a lot at 11pm and
/// listing it is where live buying quietly stops being profitable: by the time the seller opens the
/// listing editor, the screen that knew the item name, what it cost all in and what eBay pays for it
/// has been closed. So they type the title from memory, guess the price, and never enter a cost.
/// </para>
/// <para>
/// <b>Nothing here is a new opinion about money.</b> The asking price is
/// <see cref="WonLot.ResalePrice"/> — the card's own comps-derived resale figure — put through the
/// same <see cref="InventoryHealthAnalyzer.Charm"/> rounding the repricer and the relister use, with
/// the lot's landed cost as the floor so the rounding can never take the ask under what the thing
/// cost. A lot nothing could price gets <b>no price</b> rather than an invented one, and the result
/// says so.
/// </para>
/// <para>
/// <b>Two writes, one object.</b> The draft is what the seller edits and publishes; the deal card is
/// what carries the cash already spent into <see cref="CostBasisStore"/> — but only when the listing
/// goes live and has an ID, which is the existing pipeline's job, not this one's. Both carry the
/// same minted SKU so they are provably about the same lot, and the deal is keyed on
/// <see cref="WonLot.Id"/> so listing the same row twice updates one card instead of reporting the
/// capital twice.
/// </para>
/// <para>
/// <b>What it refuses to invent.</b> The condition, the photos, the item specifics and the
/// description body are not knowable from a live feed, and a draft that guessed them would publish a
/// claim about somebody's item. The draft carries the app's own defaults and the result names what
/// the seller still has to decide.
/// </para>
/// <para>
/// Pure and static: no file, no database, no clock of its own. Everything that touches disk lives in
/// <see cref="DraftStore"/> and <see cref="DealStore"/>, which already existed.
/// </para>
/// </remarks>
public static class WonLotListing
{
    /// <summary>eBay's own limit. A title longer than this is rejected at publish, so it is cut
    /// here where the seller can see what was cut.</summary>
    public const int MaxTitleLength = 80;

    /// <summary>How this app's live-buy SKUs are prefixed, so a glance at a cost-basis row says
    /// where the number came from.</summary>
    public const string SkuPrefix = "WN";

    /// <summary>What the deal board calls these, and the key it de-duplicates them on.</summary>
    public const string DealSource = "whatsnot";

    public const string DealSourceLabel = "Live show";

    // ── The pieces ────────────────────────────────────────────────────────────

    /// <summary>
    /// The item name, cut to what eBay accepts — on a word boundary when there is one near the end,
    /// because a title that stops mid-model-number is worse than a shorter one.
    /// </summary>
    public static string Title(WonLot lot)
    {
        ArgumentNullException.ThrowIfNull(lot);

        var title = string.Join(' ', (lot.Item ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (title.Length <= MaxTitleLength) return title;

        var cut = title[..MaxTitleLength];
        var lastSpace = cut.LastIndexOf(' ');
        // Only back up to a word boundary if that still leaves most of the title. Cutting a
        // one-word 90-character part number back to nothing would be worse than a hard cut.
        return (lastSpace >= MaxTitleLength * 3 / 4 ? cut[..lastSpace] : cut).TrimEnd();
    }

    /// <summary>
    /// The SKU both the draft and the deal card carry. Dated so a night's buys sort together, and
    /// carrying the row's own id so two identical items won in one show are two SKUs.
    /// </summary>
    public static string Sku(WonLot lot)
    {
        ArgumentNullException.ThrowIfNull(lot);
        var when = lot.WonAtUtc == default ? DateTime.UtcNow : lot.WonAtUtc;
        var tail = (lot.Id ?? "").Replace("-", "");
        tail = tail.Length >= 6 ? tail[..6] : Guid.NewGuid().ToString("N")[..6];
        return $"{SkuPrefix}-{when:yyyyMMdd}-{tail.ToUpperInvariant()}";
    }

    /// <summary>
    /// What to ask for it. The comps' resale price, charm-rounded down to the .99 that outsells a
    /// round number — but never below the lot's landed cost, which is the one line this rounding is
    /// not allowed to cross. Null when nothing priced the lot.
    /// </summary>
    public static decimal? AskingPrice(WonLot lot)
    {
        ArgumentNullException.ThrowIfNull(lot);
        if (!lot.Priced || lot.ResalePrice is not { } resale || resale <= 0m) return null;

        // The floor is what the lot cost all in. Charm leaves the price alone rather than round
        // under it — a listing that asks less than the item cost is not a price, it is a donation.
        return InventoryHealthAnalyzer.Charm(Math.Round(resale, 2), floorPrice: lot.LandedCost);
    }

    /// <summary>
    /// The description the seller starts from. Deliberately thin: the title, and an invitation to
    /// ask. Everything else about a used item — what works, what is scratched, what is missing — is
    /// knowable only by the person holding it, and a generated paragraph that guessed would be a
    /// claim made to a buyer on their behalf.
    /// </summary>
    public static string Description(WonLot lot)
    {
        ArgumentNullException.ThrowIfNull(lot);
        return $"{Title(lot)}\n\n" +
               "Please see the photos for condition, and ask any questions before buying.";
    }

    /// <summary>
    /// What the draft could not decide. Said out loud at the moment the draft is made, rather than
    /// found out when eBay rejects the publish or a buyer opens a case about the condition.
    /// </summary>
    public static List<string> Notes(WonLot lot)
    {
        ArgumentNullException.ThrowIfNull(lot);

        var notes = new List<string>();

        if (AskingPrice(lot) is null)
        {
            notes.Add("Nothing on eBay priced this lot, so the draft has no price on it — " +
                      "set one before you publish.");
        }

        notes.Add("Set the condition and add photos. Neither is knowable from a live feed, so the " +
                  "draft opens on the app's defaults.");

        if ((lot.Item ?? "").Trim().Length > MaxTitleLength)
            notes.Add($"The title was cut to eBay's {MaxTitleLength} characters — check nothing that matters came off the end.");

        return notes;
    }

    // ── The draft ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The lot as a local draft, ready for the listing editor. Saved by
    /// <see cref="DraftStore.SaveDraft"/>, which mints the filename.
    /// </summary>
    public static DraftFile Draft(WonLot lot) => Draft(lot, Sku(lot));

    /// <summary>
    /// The same draft, carrying a SKU decided by the caller.
    /// </summary>
    /// <remarks>
    /// The SKU is minted once and handed to both this and <see cref="Deal(WonLot, DateTime, string)"/>,
    /// so the file on the desktop and the card on the board provably describe one lot. Minting it
    /// twice was a real hazard rather than a tidiness point: <see cref="Sku"/> falls back to a fresh
    /// GUID for a lot whose id is too short to use, so two calls could hand back two different codes
    /// and the join between the listing and its cost would silently not exist.
    /// </remarks>
    public static DraftFile Draft(WonLot lot, string sku)
    {
        ArgumentNullException.ThrowIfNull(lot);

        var title = Title(lot);
        var price = AskingPrice(lot);

        return new DraftFile
        {
            Title = title,
            Data = new PostListingRequest
            {
                Title = title,
                Description = Description(lot),
                // What the item cost is already known — it was won an hour ago — and this is the key
                // that carries it onto eBay. The publish sends this SKU, and the cost basis is
                // written the moment eBay answers with a listing ID, instead of waiting for the
                // seller to type it onto a card at midnight.
                Sku = SellerSku.Sanitize(sku),
                // No price at all rather than a zero pretending to be one. The editor shows an
                // empty box, which is what "you decide this" looks like.
                Price = price ?? 0m,
                Quantity = 1,
                ListingFormat = "FIXED_PRICE",
                DurationDays = 30,
                // No category. What the card classified this into is a RESALE category — the thing
                // that decides which sold comps count — not an eBay category id, and writing one
                // into the other would file the listing in the wrong department silently. The
                // editor's own suggester reads the title, which is now filled in for it.
            },
        };
    }

    // ── The deal card ─────────────────────────────────────────────────────────

    /// <summary>
    /// The lot as a deal at <see cref="DealStages.Bought"/> — cash already gone, item in hand.
    /// </summary>
    /// <remarks>
    /// The split matters: <c>PurchasePrice</c> is the hammer price plus the platform's premium, and
    /// the shipping goes in <c>PurchaseExtraCost</c>, which is exactly how
    /// <see cref="CostBasisStore"/> wants a unit cost and its inbound freight when the pipeline
    /// writes one at Listed. Together they are the row's landed cost, so the board's capital figure
    /// and the buy sheet's spend are the same money.
    /// </remarks>
    public static DealUpsertRequest Deal(WonLot lot, DateTime nowUtc) => Deal(lot, nowUtc, Sku(lot));

    /// <summary>The same card, carrying the SKU its draft carries.</summary>
    public static DealUpsertRequest Deal(WonLot lot, DateTime nowUtc, string sku)
    {
        ArgumentNullException.ThrowIfNull(lot);

        return new DealUpsertRequest
        {
            Stage = DealStages.Bought,
            Title = Title(lot),
            Source = DealSource,
            SourceLabel = DealSourceLabel,
            // The row's own id, so listing the same lot twice updates one card instead of counting
            // the same capital again.
            SourceItemId = lot.Id,
            Quantity = 1,

            PurchasePrice = Math.Round(lot.WinningBid + lot.BuyerFee, 2),
            PurchaseExtraCost = Math.Round(lot.ShippingCost, 2),
            BoughtUtc = lot.WonAtUtc == default ? nowUtc : lot.WonAtUtc,

            // The forecast that justified the bid, frozen with the card that made it. The board
            // grades these later against what actually happened, which only works if this is the
            // number the seller acted on rather than one recomputed afterwards.
            ProjectedSalePrice = lot.ResalePrice,
            ProjectedNetProfit = lot.ProjectedProfit,
            ProjectedRoiPercent = lot.ProjectedRoiPercent,
            ProjectedDaysToCash = lot.DaysToCash,
            ProjectedBasis = ProjectedBasis(lot),

            // What the app said not to go past. The board already flags a deal bought over its own
            // maximum, so tonight's discipline survives the sheet being cleared.
            MaxBuyPrice = lot.CeilingAtWin > 0m ? lot.CeilingAtWin : null,

            Sku = SellerSku.Sanitize(sku),
            Note = $"Won on a live show{(string.IsNullOrWhiteSpace(lot.CategoryLabel) ? "" : $" · {lot.CategoryLabel}")}",
        };
    }

    /// <summary>Where the projection came from, in the evidence's own terms.</summary>
    public static string ProjectedBasis(WonLot lot)
    {
        ArgumentNullException.ThrowIfNull(lot);
        if (!lot.Priced) return "No eBay sold comps — unpriced at the hammer";

        // The rate is already a percentage on the card (80m is 80%), so it is written as it stands.
        var basis = $"eBay sold comps — {lot.CompCount} sold";
        if (lot.SellThroughRate is { } rate) basis += $", {Math.Round(rate):0}% sell-through";
        return basis;
    }

    // ── The sentence ──────────────────────────────────────────────────────────

    /// <summary>
    /// What just happened, in one line, for the live region above the card. Rounds the ask DOWN,
    /// like every other figure this screen speaks: the seller should never hear a bigger number
    /// than the draft actually carries.
    /// </summary>
    public static string Say(WonLot lot, decimal? price, bool alreadyListed, bool onDealBoard)
    {
        ArgumentNullException.ThrowIfNull(lot);

        var what = alreadyListed ? "Already drafted" : "Draft saved";
        var line = price is { } ask
            ? $"{what}: {Title(lot)} at {Math.Floor(ask).ToString("C0")}, the price the sold comps support."
            : $"{what}: {Title(lot)}, with no price on it — nothing on eBay priced this lot.";

        // Only claimed when it is true. The board write can be refused, and a screen that said it
        // happened anyway would leave the seller believing tonight's capital is being tracked.
        return onDealBoard ? line + " It's on the deal board too, at what you paid." : line;
    }
}
