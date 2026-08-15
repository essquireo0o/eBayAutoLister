namespace ING_eBay_AutoLister.Models;

// ── Sending it, and what Amazon actually said back ────────────────────────────────────────────
//
// Phase 3 built the payload. This is the phase that sends it, and the whole difficulty is in one
// sentence: ON AMAZON, A 200 DOES NOT MEAN THE LISTING EXISTS.
//
// eBay is synchronous. AddFixedPriceItem answers with an item ID, the listing is live, and the
// verdict in the response IS the verdict. Amazon's Listings Items API answers a submission the way
// a post office answers a parcel: it took it. Validation happens afterwards, on Amazon's schedule,
// and the listing can fail then — with the reason attached to the SKU rather than returned to the
// caller, because the caller left minutes ago.
//
// So three states that an eBay-shaped model would collapse are kept apart here:
//
//   1. AMAZON TOOK THE SUBMISSION       (HTTP 200, status ACCEPTED)  → Submitted. NOT live.
//   2. AMAZON REFUSED IT ON THE SPOT    (HTTP 200, status INVALID)   → Rejected. Note the 200.
//   3. AMAZON TOOK IT AND FAILED IT LATER                            → only a later GET knows.
//
// The second is the trap worth naming twice. A rejection arrives as a SUCCESSFUL HTTP response with
// a status field that says INVALID and the reasons in an issues array. Code that checks
// IsSuccessStatusCode and moves on reports a rejected listing as a published one.
//
// Nothing in this file has a "published" or "live" state, and that is deliberate rather than an
// omission. This app never observes a listing going live — the closest true statement it can make
// is that Amazon's own summary for the SKU currently says BUYABLE, which is Amazon's word, reported
// as a quotation. See AmazonListingState.

/// <summary>
/// An offer on a product Amazon's catalogue already has — the common case, and the first path.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately NOT built from an eBay draft the way <see cref="AmazonFillRequest"/> is. An offer
/// says "the thing at this ASIN — I have some, at this price, in this condition", and every one of
/// those is a fact about the SELLER's stock rather than about the product. The draft cannot answer
/// them and must not appear to: see <c>AmazonListingMapper.NeverInvent</c>, which already refuses
/// <c>merchant_suggested_asin</c> and <c>fulfillment_availability</c> for exactly this reason.
/// </para>
/// <para>
/// So every field here is stated outright by whoever is listing, and each missing one comes back
/// named rather than defaulted. <see cref="Quantity"/> is nullable for that reason and not by
/// accident — zero is a legitimate answer meaning "out of stock", so a plain int could not tell
/// "none in stock" from "nobody said", and defaulting it to 1 would publish a stock level this app
/// invented.
/// </para>
/// </remarks>
public sealed class AmazonOfferRequest
{
    /// <summary>The catalogue product this offer attaches to. Ten characters, and never guessed.</summary>
    public string Asin { get; set; } = "";

    /// <summary>
    /// The seller's own identifier for this offer.
    /// </summary>
    /// <remarks>
    /// Required, and never generated. A submission is a PUT at <c>(seller, SKU)</c>, so a SKU that
    /// is already in use is not a name collision — it REPLACES that listing's price, condition and
    /// stock. An invented SKU is therefore a coin flip between creating an offer and overwriting an
    /// existing one, and the seller is the only one who knows which of their SKUs are free.
    /// </remarks>
    public string Sku { get; set; } = "";

    /// <summary>Amazon's condition token — <c>new_new</c>, <c>used_good</c>. Validated, never mapped by guess.</summary>
    public string Condition { get; set; } = "";

    /// <summary>The seller's note about the condition. Optional, and the only free text here.</summary>
    public string ConditionNote { get; set; } = "";

    /// <summary>What the buyer pays, tax included, in <see cref="Currency"/>.</summary>
    public decimal? Price { get; set; }

    public string Currency { get; set; } = "";

    /// <summary>How many the seller has. Null means nobody said; 0 means none — see the remarks above.</summary>
    public int? Quantity { get; set; }

    /// <summary>
    /// Who ships it. <c>DEFAULT</c> is the seller; an Amazon-fulfilled code names a warehouse.
    /// </summary>
    public string FulfillmentChannelCode { get; set; } = "";

    /// <summary>
    /// The catalogue product's own product type, when the seller knows it.
    /// </summary>
    /// <remarks>
    /// Defaulted to <c>PRODUCT</c>, Amazon's generic root type, which is what an offer-only
    /// submission may use when the specific one is not to hand. Naming the real one is better and
    /// the Catalog Items API is where it comes from — but a wrong specific type is worse than the
    /// generic one, so the generic is the default rather than a guess at the category.
    /// </remarks>
    public string ProductType { get; set; } = "";
}

/// <summary>
/// A whole new catalogue product, built from an eBay draft — the second path.
/// </summary>
/// <remarks>
/// The offer half of a listing is five facts. The product half is a whole schema, which is what
/// Phase 3 already fills, so this carries the same body <see cref="AmazonFillRequest"/> does and
/// adds only the SKU to put it at. It is second for a reason: creating a catalogue entry that
/// duplicates an existing one is a merge request and a suppressed listing, whereas an offer on an
/// existing ASIN cannot create anything.
/// </remarks>
public sealed class AmazonProductSubmitRequest
{
    /// <summary>The draft and the product type, exactly as the fill endpoint takes them.</summary>
    public AmazonFillRequest Draft { get; set; } = new();

    /// <summary>The seller's identifier for the new listing. Required, never generated — see AmazonOfferRequest.Sku.</summary>
    public string Sku { get; set; } = "";

    public int? Quantity { get; set; }

    public string FulfillmentChannelCode { get; set; } = "";
}

// ── What came back ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One thing Amazon has to say about a submission or a SKU.
/// </summary>
/// <remarks>
/// <see cref="Severity"/> is the field that decides whether a listing is dead or merely imperfect,
/// and Amazon attaches both to the same array. A WARNING is a listing that went up with something
/// worth fixing; an ERROR is one that did not go up at all. Reporting the count of "issues" without
/// the split tells a seller their accepted listing has four problems when it has four suggestions.
/// </remarks>
public sealed class AmazonSubmissionIssue
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";

    /// <summary>ERROR | WARNING | INFO, as Amazon spelled it.</summary>
    public string Severity { get; set; } = "";

    /// <summary>The attributes the issue is about, when Amazon named them.</summary>
    public List<string> AttributeNames { get; set; } = [];

    /// <summary>True when this issue alone means there is no listing.</summary>
    public bool IsError => string.Equals(Severity, "ERROR", StringComparison.OrdinalIgnoreCase);

    public bool IsWarning => string.Equals(Severity, "WARNING", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What happened to a submission. There is no <c>published</c> member, on purpose.
/// </summary>
public static class AmazonSubmissionState
{
    /// <summary>This deployment cannot call Amazon. Nothing was sent.</summary>
    public const string NotConfigured = "not_configured";

    /// <summary>The app refused to send it — a required fact was missing, or the target was production.</summary>
    public const string Blocked = "blocked";

    /// <summary>
    /// Amazon took the submission. <b>This is not a live listing</b>; it is a queued one.
    /// </summary>
    public const string Submitted = "submitted";

    /// <summary>Amazon refused it. Reached through an HTTP 200 as often as through a 4xx.</summary>
    public const string Rejected = "rejected";

    /// <summary>Amazon could not be reached, or answered something unreadable.</summary>
    public const string Error = "error";
}

/// <summary>
/// The request that was made and the answer that came back, kept for a person to read.
/// </summary>
/// <remarks>
/// <para>
/// Carried on every submission because "Amazon rejected it" is not a fact anyone can act on without
/// the exchange behind it. Amazon's issue codes are numbers, its messages quote attribute paths, and
/// the first thing anyone debugging one asks is what was actually sent.
/// </para>
/// <para>
/// <see cref="Url"/> has the seller ID taken out of it. It is the merchant token, it identifies the
/// account rather than describing the call, and this record is written into reports and logs that
/// travel further than the request did. Everything else about the URL — the host above all, which is
/// what proves sandbox rather than production — is left exactly as sent.
/// </para>
/// </remarks>
public sealed class AmazonCall
{
    public string Method { get; set; } = "";

    /// <summary>The full URL as sent, with the seller ID replaced by <c>{sellerId}</c>.</summary>
    public string Url { get; set; } = "";

    /// <summary>The JSON body as sent, or empty for a GET.</summary>
    public string RequestBody { get; set; } = "";

    public int? HttpStatus { get; set; }

    /// <summary>Amazon's answer, verbatim. Never truncated: the issues are at the end of it.</summary>
    public string ResponseBody { get; set; } = "";

    /// <summary>Amazon's <c>x-amzn-RequestId</c>. The only thing Amazon's support will act on.</summary>
    public string RequestId { get; set; } = "";
}

/// <summary>
/// The result of putting one listing to Amazon.
/// </summary>
public sealed class AmazonSubmission
{
    /// <summary>See <see cref="AmazonSubmissionState"/>.</summary>
    public string State { get; set; } = AmazonSubmissionState.Error;

    /// <summary>Amazon's own word — ACCEPTED | INVALID — or empty when it never got that far.</summary>
    public string AmazonStatus { get; set; } = "";

    public string Sku { get; set; } = "";

    /// <summary>Amazon's handle for this submission. Worth keeping; it is what a support case cites.</summary>
    public string SubmissionId { get; set; } = "";

    /// <summary>Which Amazon this went to. Reported so a reader never has to assume.</summary>
    public string Environment { get; set; } = "";

    public string ProductType { get; set; } = "";
    public string MarketplaceId { get; set; } = "";

    public List<AmazonSubmissionIssue> Issues { get; set; } = [];

    /// <summary>The exchange itself. Null only when nothing was sent.</summary>
    public AmazonCall? Call { get; set; }

    /// <summary>One sentence, and never one that claims the listing is live.</summary>
    public string Headline { get; set; } = "";

    /// <summary>What to do next, in the seller's terms.</summary>
    public string NextAction { get; set; } = "";

    public IEnumerable<AmazonSubmissionIssue> Errors => Issues.Where(i => i.IsError);
    public IEnumerable<AmazonSubmissionIssue> Warnings => Issues.Where(i => i.IsWarning);

    /// <summary>
    /// True when Amazon has the submission and has not yet said what became of it.
    /// </summary>
    /// <remarks>
    /// The name is the point. There is no <c>Published</c> or <c>IsLive</c> anywhere on this type,
    /// because at the moment this object exists no such fact is known — the only honest reading of an
    /// ACCEPTED is that Amazon will decide later, and <see cref="AmazonListingState"/> is how anyone
    /// finds out what it decided.
    /// </remarks>
    public bool AwaitingAmazon => State == AmazonSubmissionState.Submitted;
}

// ── Asking, afterwards, what became of it ─────────────────────────────────────────────────────

/// <summary>
/// What Amazon says about a SKU now — the half of the truth the submission response cannot carry.
/// </summary>
/// <remarks>
/// This is the answer to "did it actually work?", and it is a separate call because on Amazon it is
/// a separate question, asked minutes later. A submission that came back ACCEPTED with an empty
/// issues array can be sitting here with an ERROR against it and no <see cref="Statuses"/> at all.
/// </remarks>
public sealed class AmazonListingState
{
    /// <summary>ok | not_configured | not_found | error.</summary>
    public string Status { get; set; } = "ok";

    public string Message { get; set; } = "";

    public string Sku { get; set; } = "";

    /// <summary>The catalogue product Amazon attached the offer to, once it has.</summary>
    public string Asin { get; set; } = "";

    public string ItemName { get; set; } = "";
    public string ProductType { get; set; } = "";

    /// <summary>
    /// Amazon's own status words for the SKU — <c>BUYABLE</c>, <c>DISCOVERABLE</c>.
    /// </summary>
    /// <remarks>
    /// Reported as a list of Amazon's strings rather than folded into a boolean. They are not
    /// synonyms: DISCOVERABLE without BUYABLE is a listing a shopper can find and cannot buy, which
    /// is a real and confusing state that "live: true" would erase.
    /// </remarks>
    public List<string> Statuses { get; set; } = [];

    public List<AmazonSubmissionIssue> Issues { get; set; } = [];

    public AmazonCall? Call { get; set; }

    public string Headline { get; set; } = "";

    public IEnumerable<AmazonSubmissionIssue> Errors => Issues.Where(i => i.IsError);
    public IEnumerable<AmazonSubmissionIssue> Warnings => Issues.Where(i => i.IsWarning);

    /// <summary>True when Amazon lists this SKU as buyable. Amazon's claim, not this app's.</summary>
    public bool AmazonSaysBuyable =>
        Statuses.Any(s => string.Equals(s, "BUYABLE", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when something is stopping this listing rather than merely noted against it.</summary>
    public bool HasErrors => Issues.Any(i => i.IsError);
}
