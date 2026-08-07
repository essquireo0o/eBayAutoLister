namespace ING_eBay_AutoLister.Models;

// ── "What is on the block right now?" (see Services/WhatnotShowParser.cs) ─────────────────────
// The WhatsNot card prices whatever it is given, and until now the seller gave it the item by typing
// it while an auctioneer talked over them. This is the read that fills those two boxes for them: the
// lot's name and what it is standing at, taken off the show's own page.
//
// Everything here is a claim about somebody else's document, so every field says where it came from
// and the read says out loud what it is not: a snapshot, taken once, of a number that moves. Nothing
// here prices anything — see WhatnotShowRead.

/// <summary>The show to read. See <c>POST /api/whatsnot/read</c>.</summary>
public sealed class WhatnotReadRequest
{
    /// <summary>The show's page — the address in the browser panel, or pasted from a real browser.</summary>
    public string? Url { get; set; }
}

/// <summary>The answers a read can give. Spelled once so the endpoint, the screen and the tests agree.</summary>
public static class WhatnotReadStatuses
{
    /// <summary>A lot was found, with a name worth searching on.</summary>
    public const string Read = "read";

    /// <summary>The page was read and there was no lot on it worth pricing — which is the normal
    /// state of a live show between lots, and is not a failure.</summary>
    public const string NoListing = "no-listing";

    /// <summary>A Whatnot address that is not one show — the browse page, a profile, a category.</summary>
    public const string NotAShow = "not-a-show";

    /// <summary>Whatnot answered, but with something this app can't read: a sign-in page, a
    /// challenge, a layout that no longer carries the listing.</summary>
    public const string Unreadable = "unreadable";

    /// <summary>The read never landed — refused, timed out, or the network is down.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>Not an address this read can be pointed at.</summary>
    public const string Invalid = "invalid";
}

/// <summary>
/// One live show's page, read for the lot that is on the block.
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately no <c>MaxBid</c> or <c>ResalePrice</c> on this object, and a test holds it
/// that way. What a lot is WORTH is one path in this app — the eBay sold-comp pipeline behind
/// <c>/api/whatsnot/bid</c> — and a second number of the same shape arriving from a page read is how
/// a screen ends up holding two opinions about one item.
/// </para>
/// <para>
/// <see cref="Evidence"/> is not decoration. This reads somebody else's page, whose shape is theirs
/// to change without telling anyone. A title that arrived from <c>...activeListing.title</c> and a
/// title that arrived from a stray <c>name</c> three levels away are worth different amounts of
/// trust, and the screen shows which one it got.
/// </para>
/// </remarks>
public sealed class WhatnotShowRead
{
    /// <summary>See <see cref="WhatnotReadStatuses"/>.</summary>
    public string Status { get; set; } = WhatnotReadStatuses.Unreadable;

    /// <summary>The address that was read, after the scheme was filled in.</summary>
    public string Url { get; set; } = "";

    /// <summary>What goes in the item box: the lot's name, cleaned by the same
    /// <see cref="Services.LiveLotList.Clean"/> a pasted lot line goes through, so a lot read off the
    /// page and the same lot pasted by hand reach the comp lookup identically. Empty when nothing on
    /// the page was searchable.</summary>
    public string Title { get; set; } = "";

    /// <summary>The name exactly as the page wrote it. Kept because a clean that dropped the wrong
    /// half of a name is only findable next to the original.</summary>
    public string RawTitle { get; set; } = "";

    /// <summary>What goes in the bid box. Null when the page carried no price — the ceiling still
    /// answers without it, it just cannot say how much room is left.</summary>
    public decimal? CurrentBid { get; set; }

    /// <summary>Which key on the page the price came from, e.g. <c>currentPrice</c>. A number about
    /// money off somebody else's schema should say which field it was.</summary>
    public string BidKey { get; set; } = "";

    /// <summary>Whether a price was found at all.</summary>
    public bool BidIsFromThePage => CurrentBid is > 0m;

    /// <summary>True when the number is where the bidding STARTS rather than what anyone has bid.
    /// It still belongs in the box — it is the right starting point — but it is not a live number.</summary>
    public bool BidIsOpeningPrice { get; set; }

    /// <summary>The lot's photo, https only — see <see cref="Services.WhatnotShowParser"/>. Nothing
    /// is priced off it; it answers the one question the title can't, which is whether this is the
    /// thing actually on screen. It is also what a vision pass would be pointed at.</summary>
    public string ImageUrl { get; set; } = "";

    /// <summary>What the show calls this lot's category. A hint for the seller's eyes; it is not fed
    /// to the pricing call as a category id, because the two vocabularies are not the same one.</summary>
    public string CategoryHint { get; set; } = "";

    /// <summary>The lot's own id on the show, when the page carried one. The screen uses it for one
    /// thing: telling "the show moved to a new lot" apart from "the same lot, redrawn".</summary>
    public string ListingId { get; set; } = "";

    /// <summary>One line for the strip: what was found, or why nothing was.</summary>
    public string Headline { get; set; } = "";

    /// <summary>The honest limits of the read, in a sentence. On a successful read this still says
    /// the bid is a snapshot, because a seller who learns that from a screen that repeats it will not
    /// be caught out by it at $200.</summary>
    public string Detail { get; set; } = "";

    /// <summary>What still works when the read didn't. Never empty on a refusal — a diagnosis with no
    /// next move sends the seller back to typing without saying so.</summary>
    public string Hint { get; set; } = "";

    /// <summary>Which key in the page each field came out of. This is a claim about a document the
    /// seller cannot see, so a read that went wrong should be inspectable rather than mysterious.</summary>
    public List<string> Evidence { get; set; } = [];

    /// <summary>Things worth knowing before bidding on what was read: an opening price wearing a live
    /// number's clothes, a lot the page marks sold, a giveaway, a name that was shortened.</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>How long the read took, end to end. A live screen's budget is seconds.</summary>
    public long ElapsedMs { get; set; }

    /// <summary>What Whatnot answered the fetch with, when it answered.</summary>
    public int? HttpStatus { get; set; }
}
