using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

// ── Putting a listing to Amazon, and reading what it says back ────────────────────────────────
//
// Everything in this file is pure. A request in, a payload out; a response body in, a verdict out.
// That is not tidiness — it is the only way this phase is provable at all, because this deployment
// cannot obtain an Amazon access token (the stored LWA client secret is a 203-character note saying
// the disclosure was collapsed in a screenshot, and LWA answers it with invalid_client). The network
// half lives next door in AmazonListingSubmitService and is thin enough to read in one sitting.
//
// The judgement being encoded is that Amazon answers a submission twice, minutes apart, and the
// first answer is not the verdict:
//
//   PUT  /listings/2021-08-01/items/{seller}/{sku}   → "I have it."      (or "no, and here is why")
//   GET  /listings/2021-08-01/items/{seller}/{sku}   → "here is what became of it."
//
// AmazonSubmissionResponse reads the first. AmazonListingStateResponse reads the second. Neither of
// them is allowed to say the word "published", and AmazonSubmissionWords is where that is enforced
// rather than merely intended.

/// <summary>
/// The Listings Items API's paths.
/// </summary>
/// <remarks>
/// Paths only, never hosts — the same rule <see cref="AmazonDefinitionsApi"/> follows, and it matters
/// more here. Every other Amazon call this app makes is a read; these ones write. A path that could
/// name its own host would be a way for a submission to reach production while the app reported
/// sandbox, and on this endpoint that is a real listing on a real seller account.
/// </remarks>
public static class AmazonListingsApi
{
    /// <summary>The API version this app is written against.</summary>
    public const string Version = "2021-08-01";

    private const string Root = $"/listings/{Version}/items";

    /// <summary>Ask for the issues and the summary — what is wrong, and what state the SKU is in.</summary>
    public const string IssuesAndSummaries = "issues,summaries";

    /// <summary>The language Amazon writes its issue messages in.</summary>
    public const string IssueLocale = "en_US";

    /// <summary>Where a listing is created or replaced.</summary>
    /// <remarks>
    /// Both parts are escaped. The SKU is the seller's own string and can legitimately contain a
    /// slash or a plus — unescaped, the first turns one SKU into a path that addresses a different
    /// resource and the second silently becomes a space.
    /// </remarks>
    public static string ItemPath(string sellerId, string sku, string marketplaceId) =>
        $"{Root}/{Uri.EscapeDataString(sellerId ?? "")}/{Uri.EscapeDataString(sku ?? "")}" +
        $"?marketplaceIds={Uri.EscapeDataString(marketplaceId ?? "")}" +
        $"&issueLocale={Uri.EscapeDataString(IssueLocale)}";

    /// <summary>Where to ask what became of a submission.</summary>
    public static string StatePath(string sellerId, string sku, string marketplaceId) =>
        $"{Root}/{Uri.EscapeDataString(sellerId ?? "")}/{Uri.EscapeDataString(sku ?? "")}" +
        $"?marketplaceIds={Uri.EscapeDataString(marketplaceId ?? "")}" +
        $"&includedData={Uri.EscapeDataString(IssuesAndSummaries)}" +
        $"&issueLocale={Uri.EscapeDataString(IssueLocale)}";

    /// <summary>
    /// A URL with the seller ID taken out, for anything that will be read rather than sent.
    /// </summary>
    /// <remarks>
    /// The seller ID is the merchant token. It identifies the account rather than describing the
    /// call, and a submission report is a thing that gets pasted into a message to somebody.
    /// </remarks>
    public static string Redact(string url, string sellerId) =>
        string.IsNullOrWhiteSpace(sellerId) || string.IsNullOrWhiteSpace(url)
            ? url ?? ""
            : url.Replace(Uri.EscapeDataString(sellerId), "{sellerId}", StringComparison.Ordinal)
                 .Replace(sellerId, "{sellerId}", StringComparison.Ordinal);
}

// ── What this app refuses to send ─────────────────────────────────────────────────────────────

/// <summary>
/// Whether a submission is allowed to leave this process at all.
/// </summary>
/// <remarks>
/// <para>
/// This phase is the first Amazon code that WRITES, and the asymmetry between the two mistakes it
/// can make is total. A submission wrongly withheld costs a retry. A submission wrongly sent to
/// production creates a real listing on a real Selling Partner account — which Amazon then holds the
/// seller answerable for, and which cannot be un-created faster than a shopper can buy from it.
/// </para>
/// <para>
/// So production is refused outright here rather than guarded behind a flag. <see cref="AmazonOptions.Sandbox"/>
/// already defaults to sandbox and needs the literal <c>false</c> to leave it; this adds the rule
/// that even a deployment which has done that cannot submit, because nothing has yet asked the
/// seller whether they meant to. That consent is a later phase's job and a UI's job, and inventing
/// it here would be this app deciding on the seller's behalf that they agreed.
/// </para>
/// </remarks>
public static class AmazonSubmitGuard
{
    /// <summary>Why a submission was not sent, or null when it may be.</summary>
    public static AmazonConfigProblem? Check(AmazonOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Production is allowed only when the seller has said so IN THIS APP, and the saying is
        // kept with a date. The sandbox flag alone was never consent: it is a configuration value,
        // and configuration gets copied between machines, restored from a backup, or set by
        // somebody who was not the person answerable for the listing. See CredentialsStore's
        // AmazonProductionConsentAt, and the tick box on the Amazon page that writes it.
        if (!options.Sandbox && string.IsNullOrWhiteSpace(options.ProductionConsentAt))
            return new AmazonConfigProblem("production_refused",
                "Credentials__AmazonSandbox is false, which points every call at a real Selling Partner account, " +
                "and a submission there creates a real listing the seller is answerable for. Nobody has agreed to " +
                "that in this app.",
                "Open the Amazon page and tick \"submissions may create real listings on my Amazon account\", or " +
                "set Credentials__AmazonSandbox back to true to keep working against the sandbox.");

        return options.CallProblem;
    }
}

/// <summary>
/// Whether an offer describes something Amazon can be asked about.
/// </summary>
/// <remarks>
/// Every check here refuses rather than fills in. That is the same ethic as
/// <see cref="AmazonListingMapper.NeverInvent"/> and it costs more on this path, because these are
/// the five short fields that a helpful default would fit perfectly: a quantity of 1, a condition of
/// new, a currency of USD. Each of those defaults is a claim about someone's stock, someone's goods
/// or someone's price, published under their account — and a wrong one is discovered by a buyer.
/// Currency is the single exception and is defaulted, because it is a property of the marketplace
/// rather than of the seller: on amazon.com a price is in dollars or it is not a price.
/// </remarks>
public static class AmazonOfferCheck
{
    /// <summary>Amazon's longest SKU. A longer one is refused by the API, not truncated.</summary>
    public const int MaxSkuLength = 40;

    /// <summary>Every ASIN is exactly this long — ten characters, letters and digits.</summary>
    public const int AsinLength = 10;

    /// <summary>
    /// The condition tokens Amazon publishes, used to catch a typo before it costs a round trip.
    /// </summary>
    /// <remarks>
    /// <b>Not the authority.</b> The legal set is per product type and comes from that type's schema
    /// — a category may publish fewer, and Amazon rejects a token that is real but not offered there.
    /// This list exists only so that <c>used_god</c> is caught here with a sentence rather than at
    /// Amazon as issue 4000001, and a token outside it is refused rather than corrected to a
    /// neighbour.
    /// </remarks>
    public static readonly IReadOnlySet<string> KnownConditions = new HashSet<string>(StringComparer.Ordinal)
    {
        "new_new", "new_open_box", "new_oem",
        "refurbished_refurbished",
        "used_like_new", "used_very_good", "used_good", "used_acceptable",
        "collectible_like_new", "collectible_very_good", "collectible_good", "collectible_acceptable",
        "club_club",
    };

    /// <summary>The first thing missing or wrong, or null when the offer can be sent.</summary>
    public static AmazonConfigProblem? Check(AmazonOfferRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var asin = (request.Asin ?? "").Trim().ToUpperInvariant();
        if (asin.Length == 0)
            return new("no_asin",
                "No ASIN was given, so there is no catalogue product for this offer to attach to.",
                "Find the product on Amazon and use its ASIN. Nothing in an eBay draft is one, and a guess " +
                "attaches your offer — your price, your stock, your feedback — to somebody else's product.");

        if (asin.Length != AsinLength || !asin.All(char.IsAsciiLetterOrDigit))
            return new("bad_asin",
                $"\"{asin}\" is not an ASIN. Every ASIN is exactly {AsinLength} letters and digits.",
                "Check it against the product page — it is in the URL after /dp/. A mistyped ASIN that happens " +
                "to exist is an offer on the wrong product, which reads as an accepted listing.");

        var sku = (request.Sku ?? "").Trim();
        if (sku.Length == 0)
            return new("no_sku",
                "No SKU was given. A submission is addressed as (seller, SKU), so without one there is nowhere " +
                "to put the offer.",
                "Choose a SKU you are not already using. This app will not invent one: a submission REPLACES " +
                "whatever is at that SKU, so an invented one may overwrite a listing you already have.");

        if (sku.Length > MaxSkuLength)
            return new("sku_too_long",
                $"The SKU is {sku.Length} characters and Amazon's limit is {MaxSkuLength}.",
                "Shorten it. It is not truncated for you, because a truncated SKU is a different SKU and may " +
                "already belong to another listing.");

        var condition = (request.Condition ?? "").Trim();
        if (condition.Length == 0)
            return new("no_condition",
                "No condition was given, and Amazon requires one on every offer.",
                "State Amazon's own token — new_new for new, used_good and so on. This app will not assume new.");

        if (!AmazonOfferCheck.KnownConditions.Contains(condition))
            return new("unknown_condition",
                $"\"{condition}\" is not one of Amazon's condition tokens.",
                "Use one of: " + string.Join(", ", KnownConditions.Take(6)) + ", …. The exact set a product type " +
                "accepts is in its schema, and it can be narrower than this list.");

        if (request.Price is not { } price)
            return new("no_price",
                "No price was given, so there is nothing to offer the item at.",
                "State the price the buyer pays. This app will not carry one over from anywhere: a price is the " +
                "one field where a stale value costs money on every sale.");

        if (price <= 0)
            return new("bad_price",
                $"The price is {price.ToString(CultureInfo.InvariantCulture)}, which Amazon will not accept.",
                "State a price above zero.");

        if (request.Quantity is not { } quantity)
            return new("no_quantity",
                "No quantity was given. Amazon needs to know how many are available before it can sell any.",
                "State how many you have. It is asked for rather than defaulted because 0 is a real answer " +
                "meaning out of stock, so there is no number here that could stand for \"nobody said\".");

        if (quantity < 0)
            return new("bad_quantity",
                $"The quantity is {quantity}.", "State zero or more.");

        return null;
    }
}

// ── The payload ───────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Builds the body of a Listings Items submission.
/// </summary>
/// <remarks>
/// <para>
/// Amazon takes a listing as three things: which product type's rules to judge it by, which HALF of
/// those rules apply, and the attributes themselves. The second is the one with no eBay counterpart
/// and it is what separates the two paths in this phase — <c>LISTING_OFFER_ONLY</c> says "judge the
/// price and the stock, the product already exists", and <c>LISTING</c> says "judge all of it,
/// because I am creating the product too".
/// </para>
/// <para>
/// Sending <c>LISTING</c> for an offer is the expensive mistake: Amazon then demands the full product
/// schema — a title, bullet points, dimensions, a country of origin — for a product it already has,
/// and the submission fails on a dozen attributes that were never the seller's to supply.
/// </para>
/// </remarks>
public static class AmazonOfferPayload
{
    /// <summary>Amazon's generic root product type. See <see cref="AmazonOfferRequest.ProductType"/>.</summary>
    public const string GenericProductType = "PRODUCT";

    /// <summary>Merchant-fulfilled: the seller ships it themselves.</summary>
    public const string SellerFulfilled = "DEFAULT";

    /// <summary>The currency of amazon.com. A property of the marketplace, not of the seller.</summary>
    public const string DefaultCurrency = "USD";

    private static readonly JsonSerializerOptions Compact = new();

    /// <summary>The whole request body for an offer on an existing ASIN.</summary>
    public static JsonObject BuildOffer(AmazonOfferRequest request, string marketplaceId)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new JsonObject
        {
            ["productType"]  = Pick(request.ProductType, GenericProductType),
            ["requirements"] = AmazonDefinitionsApi.RequirementsOfferOnly,
            ["attributes"]   = OfferAttributes(request, marketplaceId),
        };
    }

    /// <summary>The whole request body for a new product built from a filled draft.</summary>
    /// <remarks>
    /// The attributes are Phase 3's payload, unaltered. Adding to them here would put a value into a
    /// submission that the fill report — the thing a seller reviewed and approved — never showed.
    /// The offer attributes are merged on top because they are the seller's own facts and the fill
    /// deliberately refuses to produce them.
    /// </remarks>
    public static JsonObject BuildProduct(
        AmazonListingFill fill, AmazonProductSubmitRequest request, string marketplaceId)
    {
        ArgumentNullException.ThrowIfNull(fill);
        ArgumentNullException.ThrowIfNull(request);

        var attributes = fill.Payload.DeepClone().AsObject();

        if (request.Quantity is { } quantity)
            attributes["fulfillment_availability"] = new JsonArray
            {
                new JsonObject
                {
                    ["fulfillment_channel_code"] = Pick(request.FulfillmentChannelCode, SellerFulfilled),
                    ["quantity"] = quantity,
                },
            };

        return new JsonObject
        {
            ["productType"]  = fill.ProductType,
            ["requirements"] = AmazonDefinitionsApi.RequirementsListing,
            ["attributes"]   = attributes,
        };
    }

    /// <summary>
    /// The five attributes an offer is made of.
    /// </summary>
    /// <remarks>
    /// The shapes are Amazon's and are worth stating because none of them is guessable.
    /// <c>purchasable_offer</c> is a currency wrapping a named price wrapping a schedule, because the
    /// same offer can carry a sale price with a start and an end date; a plain number is rejected.
    /// <c>fulfillment_availability</c> is deliberately outside that envelope — it carries no
    /// marketplace and no language, because stock is a fact about a warehouse rather than about a
    /// listing's presentation, and stamping the selectors on it is an attribute Amazon did not ask for.
    /// </remarks>
    public static JsonObject OfferAttributes(AmazonOfferRequest request, string marketplaceId)
    {
        ArgumentNullException.ThrowIfNull(request);

        var market   = string.IsNullOrWhiteSpace(marketplaceId)
            ? AmazonListingMapper.FallbackMarketplaceId : marketplaceId.Trim();
        var currency = Pick(request.Currency, DefaultCurrency);

        var attributes = new JsonObject
        {
            ["merchant_suggested_asin"] = new JsonArray
            {
                new JsonObject
                {
                    ["value"] = (request.Asin ?? "").Trim().ToUpperInvariant(),
                    ["marketplace_id"] = market,
                },
            },
            ["condition_type"] = new JsonArray
            {
                new JsonObject
                {
                    ["value"] = (request.Condition ?? "").Trim(),
                    ["marketplace_id"] = market,
                },
            },
            ["purchasable_offer"] = new JsonArray
            {
                new JsonObject
                {
                    ["currency"] = currency,
                    ["marketplace_id"] = market,
                    ["our_price"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["schedule"] = new JsonArray
                            {
                                new JsonObject { ["value_with_tax"] = request.Price ?? 0m },
                            },
                        },
                    },
                },
            },
        };

        if (request.Quantity is { } quantity)
            attributes["fulfillment_availability"] = new JsonArray
            {
                new JsonObject
                {
                    ["fulfillment_channel_code"] = Pick(request.FulfillmentChannelCode, SellerFulfilled),
                    ["quantity"] = quantity,
                },
            };

        // Only when the seller wrote one. An empty condition note is not a note, and Amazon reads an
        // empty string as a value rather than as an absence.
        if (!string.IsNullOrWhiteSpace(request.ConditionNote))
            attributes["condition_note"] = new JsonArray
            {
                new JsonObject
                {
                    ["value"] = request.ConditionNote.Trim(),
                    ["marketplace_id"] = market,
                    ["language_tag"] = AmazonListingMapper.LanguageTag,
                },
            };

        return attributes;
    }

    public static string ToJson(JsonNode node) => node.ToJsonString(Compact);

    private static string Pick(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

// ── Reading Amazon's answers ──────────────────────────────────────────────────────────────────

/// <summary>Reads the <c>issues</c> array, which both of Amazon's answers carry.</summary>
public static class AmazonIssueReader
{
    /// <summary>
    /// Every issue in a response body, or an empty list.
    /// </summary>
    /// <remarks>
    /// An issue with no severity is kept and left blank rather than assumed to be an ERROR or a
    /// WARNING. Guessing it upward invents a rejection; guessing it downward hides one. A blank
    /// severity shows up in the report as exactly what it is — something Amazon said without
    /// grading — which is rare enough to be worth seeing.
    /// </remarks>
    public static List<AmazonSubmissionIssue> Parse(string? json)
    {
        var issues = new List<AmazonSubmissionIssue>();
        if (string.IsNullOrWhiteSpace(json)) return issues;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return issues;
            if (!doc.RootElement.TryGetProperty("issues", out var arr) ||
                arr.ValueKind != JsonValueKind.Array) return issues;

            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;

                var issue = new AmazonSubmissionIssue
                {
                    Code     = Str(el, "code"),
                    Message  = Str(el, "message"),
                    Severity = Str(el, "severity"),
                };

                if (el.TryGetProperty("attributeNames", out var names) && names.ValueKind == JsonValueKind.Array)
                    foreach (var name in names.EnumerateArray())
                        if (name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } s)
                            issue.AttributeNames.Add(s);

                issues.Add(issue);
            }
        }
        catch (JsonException) { /* Unreadable. The caller reports the raw body, which is more use. */ }

        return issues;
    }

    internal static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

/// <summary>
/// Turns Amazon's answer to a submission into a verdict.
/// </summary>
/// <remarks>
/// <para>
/// <b>The HTTP status is not the verdict.</b> Amazon returns 200 OK for a listing it has just
/// refused, with the refusal in a <c>status</c> field and the reasons in <c>issues</c>. Any code
/// that branches on <c>IsSuccessStatusCode</c> and stops there reports that as a success — which is
/// precisely the bug this phase exists to not have, so the status field is read first and the HTTP
/// code is only consulted when there is no answer to read.
/// </para>
/// <para>
/// And an ACCEPTED is not a listing either. It means the submission is queued. The only thing that
/// knows what became of it is <see cref="AmazonListingStateResponse"/>, minutes later.
/// </para>
/// </remarks>
public static class AmazonSubmissionResponse
{
    /// <summary>Amazon took the submission for processing.</summary>
    public const string Accepted = "ACCEPTED";

    /// <summary>Amazon refused it. Arrives inside an HTTP 200 as often as not.</summary>
    public const string Invalid = "INVALID";

    /// <summary>Reads one <c>putListingsItem</c> response.</summary>
    public static AmazonSubmission Parse(string? body, int httpStatus, string fallbackSku)
    {
        var submission = new AmazonSubmission
        {
            Sku          = fallbackSku ?? "",
            Issues       = AmazonIssueReader.Parse(body),
            AmazonStatus = "",
        };

        string? status = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    status = AmazonIssueReader.Str(doc.RootElement, "status");
                    var sku = AmazonIssueReader.Str(doc.RootElement, "sku");
                    if (!string.IsNullOrWhiteSpace(sku)) submission.Sku = sku;
                    submission.SubmissionId = AmazonIssueReader.Str(doc.RootElement, "submissionId");
                }
            }
        }
        catch (JsonException) { /* Handled below: no status is its own outcome. */ }

        submission.AmazonStatus = status ?? "";

        // Amazon's own word first, whatever the HTTP code said.
        if (string.Equals(status, Invalid, StringComparison.OrdinalIgnoreCase))
        {
            submission.State = AmazonSubmissionState.Rejected;
            return submission;
        }

        if (string.Equals(status, Accepted, StringComparison.OrdinalIgnoreCase))
        {
            // An ACCEPTED with ERROR issues attached is Amazon contradicting itself, and the safe
            // reading of a contradiction about whether a listing exists is that it does not.
            submission.State = submission.Errors.Any()
                ? AmazonSubmissionState.Rejected
                : AmazonSubmissionState.Submitted;
            return submission;
        }

        // No status field. Now the HTTP code is all there is.
        submission.State = httpStatus is >= 200 and < 300
            ? AmazonSubmissionState.Error
            : httpStatus is >= 400 and < 500
                ? AmazonSubmissionState.Rejected
                : AmazonSubmissionState.Error;

        return submission;
    }
}

/// <summary>Reads <c>getListingsItem</c> — what became of a SKU after Amazon processed it.</summary>
public static class AmazonListingStateResponse
{
    public static AmazonListingState Parse(string? body, string fallbackSku)
    {
        var state = new AmazonListingState
        {
            Sku    = fallbackSku ?? "",
            Issues = AmazonIssueReader.Parse(body),
        };

        if (string.IsNullOrWhiteSpace(body)) return state;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return state;

            var sku = AmazonIssueReader.Str(doc.RootElement, "sku");
            if (!string.IsNullOrWhiteSpace(sku)) state.Sku = sku;

            if (!doc.RootElement.TryGetProperty("summaries", out var summaries) ||
                summaries.ValueKind != JsonValueKind.Array) return state;

            // One summary per marketplace. The first is the only one this single-marketplace app
            // asked for, and taking any other would report a different marketplace's verdict.
            foreach (var summary in summaries.EnumerateArray())
            {
                if (summary.ValueKind != JsonValueKind.Object) continue;

                state.Asin        = AmazonIssueReader.Str(summary, "asin");
                state.ItemName    = AmazonIssueReader.Str(summary, "itemName");
                state.ProductType = AmazonIssueReader.Str(summary, "productType");

                if (summary.TryGetProperty("status", out var statuses) &&
                    statuses.ValueKind == JsonValueKind.Array)
                    foreach (var s in statuses.EnumerateArray())
                        if (s.ValueKind == JsonValueKind.String && s.GetString() is { Length: > 0 } text)
                            state.Statuses.Add(text);

                break;
            }
        }
        catch (JsonException) { /* The caller keeps the raw body, which says more than a guess would. */ }

        return state;
    }
}

// ── Saying what happened, without overstating it ──────────────────────────────────────────────

/// <summary>
/// The sentences this phase is allowed to say.
/// </summary>
/// <remarks>
/// <para>
/// Gathered in one place so that the claim being made is reviewable as a claim, rather than scattered
/// across handlers where each one reads as reasonable on its own. The rule is the one the whole phase
/// turns on: <b>nothing here may say a listing is published, live, or up.</b>
/// </para>
/// <para>
/// It would be easy to. "Listed on Amazon" is what a seller wants to read and what the eBay path
/// truthfully says. Here it would be a guess dressed as a receipt — Amazon has taken a submission and
/// will decide later, and a seller told it is live stops watching for the rejection that arrives
/// afterwards. <see cref="ForbiddenWords"/> is asserted against every sentence this file produces.
/// </para>
/// </remarks>
public static class AmazonSubmissionWords
{
    /// <summary>Claims this phase cannot support, whatever Amazon answered.</summary>
    public static readonly string[] ForbiddenWords = ["published", "is live", "went live", "listed successfully"];

    /// <summary>The headline for a submission Amazon has taken.</summary>
    public const string Submitted =
        "Submitted, awaiting Amazon. Amazon has the submission and has not yet said what became of it — " +
        "processing happens afterwards, and a listing can still fail then with the reason attached to the SKU.";

    public static string Describe(AmazonSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);

        return submission.State switch
        {
            AmazonSubmissionState.Submitted => Submitted,

            AmazonSubmissionState.Rejected when submission.Errors.Any() =>
                $"Amazon rejected this submission: {Count(submission.Errors.Count(), "error")}. " +
                First(submission.Errors),

            AmazonSubmissionState.Rejected =>
                $"Amazon rejected this submission (status {Or(submission.AmazonStatus, "unstated")}) without " +
                "attaching a reason to it.",

            AmazonSubmissionState.Blocked =>
                "Not submitted. This app refused to send it, and nothing reached Amazon.",

            AmazonSubmissionState.NotConfigured =>
                "Not submitted. This deployment cannot call Amazon, so nothing was sent.",

            _ => "Amazon's answer could not be read as either an acceptance or a rejection, so what became of " +
                 "this submission is unknown. The response is quoted below.",
        };
    }

    /// <summary>What to do next about a submission.</summary>
    public static string NextAction(AmazonSubmission submission, string sku)
    {
        ArgumentNullException.ThrowIfNull(submission);

        return submission.State switch
        {
            AmazonSubmissionState.Submitted =>
                $"Check what became of it: GET {AmazonSubmitEndpoints.StatePath}?sku={Uri.EscapeDataString(sku ?? "")}. " +
                "Until that reports Amazon's own status for the SKU, the only true statement is that Amazon has it.",

            AmazonSubmissionState.Rejected when submission.Errors.Any() =>
                "Fix the attributes the errors name and submit again. The same SKU is correct — a submission " +
                "replaces what is at it, so a rejected one leaves nothing behind to collide with.",

            AmazonSubmissionState.Rejected =>
                "Amazon named no attribute, so the response below is the whole of what it said. Its request ID " +
                "is what Selling Partner Support will ask for.",

            _ => "Nothing was sent, so nothing on Amazon has changed.",
        };
    }

    /// <summary>The headline for what became of a SKU, once Amazon has processed it.</summary>
    /// <remarks>
    /// The buyable case quotes Amazon rather than asserting anything. "Amazon reports this SKU as
    /// BUYABLE" is checkable and survives being wrong; "the listing is live" is this app vouching for
    /// a state it has not observed and cannot re-check.
    /// </remarks>
    public static string Describe(AmazonListingState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Status == "not_found")
            return $"Amazon has no listing at SKU {state.Sku}. A submission it rejected outright leaves nothing " +
                   "behind, so this is what a rejection looks like once the submission response is gone.";

        if (state.Status != "ok") return state.Message;

        if (state.HasErrors)
            return $"Amazon rejected this listing after processing it: {Count(state.Errors.Count(), "error")} " +
                   $"against SKU {state.Sku}. {First(state.Errors)}";

        if (state.AmazonSaysBuyable)
            return $"Amazon reports SKU {state.Sku} as {string.Join(" and ", state.Statuses)}" +
                   (state.Warnings.Any() ? $", with {Count(state.Warnings.Count(), "warning")} against it" : "") +
                   ". That is Amazon's own status for the listing, read back just now.";

        if (state.Statuses.Count > 0)
            return $"Amazon reports SKU {state.Sku} as {string.Join(" and ", state.Statuses)} — present, and not " +
                   "buyable. A listing can be discoverable without being purchasable.";

        return $"Amazon has SKU {state.Sku} and gives it no status yet, which is what a submission still being " +
               "processed looks like. Nothing is wrong with it; nothing has finished either.";
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private static string First(IEnumerable<AmazonSubmissionIssue> issues)
    {
        var issue = issues.FirstOrDefault();
        if (issue is null) return "";

        var where = issue.AttributeNames.Count > 0 ? $" ({string.Join(", ", issue.AttributeNames)})" : "";
        return $"The first is {Or(issue.Code, "uncoded")}{where}: {issue.Message}";
    }

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
