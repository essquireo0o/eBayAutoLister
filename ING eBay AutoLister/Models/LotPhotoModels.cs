namespace ING_eBay_AutoLister.Models;

// ── "Is that actually what it says it is?" (see Services/LotPhotoJudge.cs) ────────────────────
// Every number on the WhatsNot card — the ceiling, the break-even, the spread, the sell-through,
// the seller's own record — is derived from one eBay sold search, and that search runs on a NAME.
// On a live show the name is whatever the host typed into the lot: "MYSTERY MINER LOT", "S19 read
// desc", "Antminer!!!". Price the name and you have priced a different product, confidently, with a
// hammer coming down.
//
// The lot's photograph is the one piece of evidence that can contradict the name, and the read
// already brings it back. This is that photograph, looked at — and nothing else. There is no price
// here and a test says so: what a lot is WORTH stays the one eBay sold-comp path behind
// /api/whatsnot/bid. This decides only what to SEARCH for, and it never decides it silently.

/// <summary>One lot photo to look at. See <c>POST /api/whatsnot/photo</c>.</summary>
public sealed class LotPhotoRequest
{
    /// <summary>The lot's photograph — the https address the show read brought back with it.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>What the item box currently says, so the look can be about the DISAGREEMENT rather
    /// than about the picture. Empty is fine and is its own answer.</summary>
    public string? Title { get; set; }
}

/// <summary>The answers a look can give. Spelled once so the endpoint, the screen and the tests agree.</summary>
public static class LotPhotoStatuses
{
    /// <summary>The photo was fetched and something in it was named.</summary>
    public const string Looked = "looked";

    /// <summary>The photo came through and nothing in it identified a product. A 480p frame of a box
    /// on a table is a real thing to be handed, and it is not a failure of anything.</summary>
    public const string Unnamed = "unnamed";

    /// <summary>Nothing to look at — no read has brought a photo back yet.</summary>
    public const string NoPhoto = "no-photo";

    /// <summary>The address answered with something that is not a picture this can send, or with
    /// more of it than this will hold.</summary>
    public const string Unreadable = "unreadable";

    /// <summary>The photo never arrived — refused, timed out, or the network is down.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>Not an address a photo can be fetched from.</summary>
    public const string Invalid = "invalid";

    /// <summary>The photo arrived and the look at it did not — no key, a rate limit, an outage.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// What the photograph says about the name the card is being priced on. This is the whole point of
/// the feature, so the five answers are spelled out rather than folded into a score.
/// </summary>
public static class LotPhotoAgreement
{
    /// <summary>The photo shows what the box says. Nothing to do, and worth saying — a check that
    /// only speaks up when it is unhappy is a check the seller learns to distrust.</summary>
    public const string Agrees = "agrees";

    /// <summary>The photo carries something the name doesn't: a model number off the plate, a
    /// capacity, a generation. This is the money case on a live show, where lots are named in a
    /// hurry by somebody holding a camera.</summary>
    public const string Sharpens = "sharpens";

    /// <summary>The photo and the name are about different products. The ceiling on the card below
    /// is then arithmetic about something that is not on screen.</summary>
    public const string Differs = "differs";

    /// <summary>The photo could not be named confidently enough to say any of the above. Never
    /// suggests a swap — a low-confidence guess replacing a real name is worse than no look at all.</summary>
    public const string Unsure = "unsure";

    /// <summary>Nothing was typed, so there is nothing to agree with — the photo's name is the only
    /// one there is.</summary>
    public const string OnlyName = "only-name";
}

/// <summary>
/// One lot photograph, looked at against the name it is being priced under.
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately no <c>MaxBid</c>, <c>ResalePrice</c> or condition-adjusted anything on this
/// object, and a test holds it that way. A photograph is evidence about IDENTITY. Letting it move a
/// dollar figure would put a second opinion about money on the one screen in this app where there is
/// no time to notice there are two — and it would be the weaker of the two, because the ceiling is
/// made of sales that happened and this is made of a compressed frame.
/// </para>
/// <para>
/// <see cref="SuggestedTitle"/> is an offer and never an action. The browser fills the item box when
/// the seller presses the button and at no other time; the automatic reader never triggers a look at
/// all. What somebody typed outranks what a model saw, every time.
/// </para>
/// </remarks>
public sealed class LotPhotoLook
{
    /// <summary>See <see cref="LotPhotoStatuses"/>.</summary>
    public string Status { get; set; } = LotPhotoStatuses.Unreadable;

    /// <summary>See <see cref="LotPhotoAgreement"/>. Empty on everything but a successful look.</summary>
    public string Agreement { get; set; } = "";

    /// <summary>The photograph that was looked at.</summary>
    public string ImageUrl { get; set; } = "";

    /// <summary>What the item box said when the look was asked for.</summary>
    public string TypedTitle { get; set; } = "";

    /// <summary>What the photo shows, as the eBay search you would type to find what it sold for —
    /// cleaned by the same <see cref="Services.LiveLotList.Clean"/> a pasted lot line goes through,
    /// so a name off a photo and the same name typed by hand reach the comp lookup identically.</summary>
    public string SeenTitle { get; set; } = "";

    public string Brand { get; set; } = "";
    public string Model { get; set; } = "";

    /// <summary>high | medium | low — how sure the look is that it named the right product. Low
    /// never suggests anything.</summary>
    public string Certainty { get; set; } = "low";

    /// <summary>The eBay condition vocabulary, from visible wear only. Shown, never costed.</summary>
    public string Condition { get; set; } = "";
    public string ConditionNote { get; set; } = "";

    /// <summary>The one thing a photo cannot answer — reframed for a live show, where the seller
    /// cannot pick the thing up but CAN type a question into the chat while it is still on the
    /// block. This is the most actionable line on the panel and it is a question, not a fact.</summary>
    public string AskTheHost { get; set; } = "";

    /// <summary>The name to price on instead, when there is one worth offering. Empty means the look
    /// has nothing better than what is already in the box — which includes every low-confidence
    /// look, and every look whose name would not survive the searchable bar.</summary>
    public string SuggestedTitle { get; set; } = "";

    /// <summary>One line for the strip: what the photo shows, or why nothing was looked at.</summary>
    public string Headline { get; set; } = "";

    /// <summary>What that means for the card below it, in a sentence.</summary>
    public string Detail { get; set; } = "";

    /// <summary>What still works when the look didn't. Never empty on a refusal.</summary>
    public string Hint { get; set; } = "";

    /// <summary>What a photograph cannot tell you about this lot. Never empty on a successful look:
    /// a picture names a product, and a live-auction bid is a claim about a working one.</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>Which part of the answer each claim came from. This is a machine's reading of a
    /// picture the seller can also see, so it should be inspectable rather than oracular.</summary>
    public List<string> Evidence { get; set; } = [];

    /// <summary>How long the look took, end to end. A live screen's budget is seconds.</summary>
    public long ElapsedMs { get; set; }

    /// <summary>What the image host answered the fetch with, when it answered.</summary>
    public int? HttpStatus { get; set; }
}
