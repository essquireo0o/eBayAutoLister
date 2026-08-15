using System.Text.Json.Nodes;

namespace ING_eBay_AutoLister.Models;

// ── What the AI already extracted, arranged the way Amazon wants it ────────────────────────────
//
// Phase 2 asked Amazon what a product needs. This is the answer to the next question: of the things
// the AI already pulled off the photos and the page, WHICH ONES ANSWER IT — and, where nothing
// does, why the listing cannot go yet.
//
// The counterpart on the eBay side is ListingReadinessResult, and the family resemblance is
// deliberate: a per-field verdict, a reason attached to every verdict, and a refusal to write a
// value nobody supplied. Three things differ, and each one is a reason this is its own file rather
// than a branch inside the eBay analyser:
//
//   1. THE TARGET IS A TREE, NOT A LIST. An eBay Item Specific is a name and a string. An Amazon
//      attribute is an array of objects, sometimes with objects inside those — a purchasable offer
//      is a currency wrapping a price list wrapping a schedule. So a filled attribute here carries
//      a JSON fragment, not a string, and the fragment is the thing that gets submitted.
//
//   2. REQUIRED IS THREE STATES, NOT TWO. Amazon has required, optional, and required-unless-you-
//      supplied-the-other-one. A product identifier is not missing when an ASIN is present; it is
//      not needed. Reporting that as a missing required field sends a seller hunting for a barcode
//      they are exempt from — see <see cref="AmazonRequirementChoice"/>.
//
//   3. A WRONG VALUE IS A SUSPENSION, NOT A BAD LISTING. eBay's punishment for a wrong Item
//      Specific is a listing that sells badly. Amazon's punishment for a fabricated GTIN or an
//      invented brand is the account. So every state below distinguishes "no value" from "a value
//      exists and Amazon will not accept it", and NOTHING in this pipeline manufactures a value to
//      make a field go green. A blocked listing that says why is the correct output.

/// <summary>
/// An eBay draft, plus which Amazon product type to read it onto.
/// </summary>
/// <remarks>
/// Inherits <see cref="ListingData"/> so the body a caller posts is the draft itself, unchanged —
/// the same choice <see cref="CrossListRequest"/> made, and for the same reason: a draft copied into
/// a second shape is a draft that can disagree with itself.
/// </remarks>
public class AmazonFillRequest : ListingData
{
    /// <summary>Amazon's product type identifier, when the seller has already picked one.</summary>
    public string ProductType { get; set; } = "";

    /// <summary>Words to search Amazon's product types with. Defaults to the draft's title.</summary>
    public string Query { get; set; } = "";

    /// <summary>
    /// Attributes the seller answered themselves, keyed by Amazon's schema property name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The way out of the deadlock <see cref="AmazonListingMapper.NeverInvent"/> creates on purpose.
    /// That list refuses to derive a regulatory declaration from a product description — a batteries
    /// declaration and a dangerous-goods declaration are the seller's legal statements, and every
    /// note on that list ends by telling the seller to make it. Until this existed there was nowhere
    /// to make it, so a correct refusal was also a dead end: no draft could ever reach
    /// <see cref="AmazonListingFill.CanSubmit"/>.
    /// </para>
    /// <para>
    /// <b>This does not weaken the rule, it completes it.</b> The rule is about what this app may
    /// conclude, and a value in here is not a conclusion — it is a person answering. Every value is
    /// still put through the schema exactly as a drafted one is, so a word outside Amazon's closed
    /// list is still <see cref="AmazonFillState.InvalidValue"/>, and the source recorded against it
    /// says a human typed it rather than naming a field it was read off.
    /// </para>
    /// </remarks>
    public Dictionary<string, string> SellerAttributes { get; set; } = [];
}

/// <summary>
/// One Amazon attribute after the draft has been read onto it.
/// </summary>
/// <remarks>
/// The counterpart of <see cref="AspectField"/>. <see cref="Payload"/> is the part that is actually
/// submitted; everything else exists so a person can see what happened and why.
/// </remarks>
public sealed class AmazonFilledAttribute
{
    /// <summary>The schema property name — <c>item_name</c>. What the payload is keyed by.</summary>
    public string Name { get; set; } = "";

    /// <summary>Amazon's own label — "Title".</summary>
    public string Title { get; set; } = "";

    public bool Required { get; set; }
    public bool ConditionallyRequired { get; set; }

    /// <summary>Which sibling satisfies the requirement instead, when it is conditional.</summary>
    public string RequirementNote { get; set; } = "";

    /// <summary>See <see cref="AmazonFillState"/>.</summary>
    public string State { get; set; } = AmazonFillState.Empty;

    /// <summary>
    /// Where the value came from, in the seller's own vocabulary — "draft title", "item specific:
    /// Color", "eBay condition NEW".
    /// </summary>
    /// <remarks>
    /// Not decoration. A seller looking at a filled Amazon form has to be able to answer "where did
    /// that come from?" without reading the mapper, because the value is going on a listing under
    /// their account and they are the one answerable for it.
    /// </remarks>
    public string Source { get; set; } = "";

    /// <summary>
    /// Why this is not filled, or what was done to the value on the way in.
    /// </summary>
    /// <remarks>
    /// Always present when <see cref="State"/> is anything other than a clean fill. "Missing" on its
    /// own is not an answer a seller can act on; "Amazon requires a declaration about lithium
    /// batteries and nothing in the draft states one" is.
    /// </remarks>
    public string Note { get; set; } = "";

    /// <summary>The values as a person reads them. More than one only for repeatable attributes.</summary>
    public List<string> Values { get; set; } = [];

    // ── What it would take to answer this one ─────────────────────────────────
    //
    // Carried so a screen can offer the right control instead of a text box for everything.
    // "Missing" is only actionable if the thing that is missing says what shape an answer takes:
    // a dangerous-goods declaration is a pick from five words Amazon publishes, and a free-text
    // box invites the seller to type a sixth and be rejected for it.

    /// <summary>Amazon's type for the value — <c>string</c>, <c>boolean</c>, <c>integer</c>, <c>number</c>.</summary>
    public string Type { get; set; } = "";

    /// <summary>True when Amazon publishes a closed list and nothing outside it is legal.</summary>
    public bool SelectionOnly { get; set; }

    /// <summary>Amazon's own tokens, when the list is closed. Empty otherwise.</summary>
    public List<string> AcceptedValues { get; set; } = [];

    /// <summary>Their display labels, in the same order, when Amazon supplied them.</summary>
    public List<string> AcceptedLabels { get; set; } = [];

    /// <summary>
    /// True when the seller can answer this attribute themselves.
    /// </summary>
    /// <remarks>
    /// False for the ones Amazon fills itself, and false for genuine composites — a nested object
    /// cannot be typed into a box, and offering one would collect a string where Amazon wants a
    /// structure and find out at submission time.
    /// </remarks>
    public bool SellerAnswerable { get; set; }

    /// <summary>
    /// Exactly what goes under <c>attributes["<see cref="Name"/>"]</c> in a Listings Items call,
    /// envelope and selectors included. Null whenever the attribute is not filled.
    /// </summary>
    public JsonNode? Payload { get; set; }

    /// <summary>True when a value was found, accepted, and is in the payload.</summary>
    public bool IsFilled => State == AmazonFillState.Filled;

    /// <summary>True when this attribute alone stops the listing being submitted.</summary>
    public bool IsBlocking =>
        State is AmazonFillState.MissingRequired or AmazonFillState.InvalidValue or AmazonFillState.TooLong;
}

/// <summary>
/// What happened to one attribute. The eBay counterpart is <see cref="AspectState"/>.
/// </summary>
public static class AmazonFillState
{
    /// <summary>A value was found, Amazon will accept its shape, and it is in the payload.</summary>
    public const string Filled = "filled";

    /// <summary>Amazon requires it outright and the draft has nothing that answers it.</summary>
    public const string MissingRequired = "missing_required";

    /// <summary>Required through an alternative, and no member of its group is filled either.</summary>
    public const string MissingConditional = "missing_conditional";

    /// <summary>Required through an alternative that a sibling already satisfied. Not needed.</summary>
    public const string SatisfiedByAlternative = "satisfied_by_alternative";

    /// <summary>Optional, and the draft says nothing about it. The ordinary resting state.</summary>
    public const string Empty = "empty";

    /// <summary>
    /// A value was found and Amazon's published list does not contain it.
    /// </summary>
    /// <remarks>
    /// Deliberately not the same as missing. The seller has an answer and Amazon disagrees about the
    /// vocabulary, which is a different job from finding an answer — and the value is withheld from
    /// the payload either way, because a value outside a closed list is a rejection.
    /// </remarks>
    public const string InvalidValue = "invalid_value";

    /// <summary>A value was found and exceeds Amazon's length limit for a field that cannot be cut.</summary>
    public const string TooLong = "too_long";
}

/// <summary>
/// One of Amazon's either/or requirements, and whether the draft satisfies it.
/// </summary>
/// <remarks>
/// Amazon writes "a product identifier OR a suggested ASIN" as a root-level <c>anyOf</c>. That is
/// one requirement with several doors, and it has to be reported as one: told that
/// <c>externally_assigned_product_identifier</c> and <c>merchant_suggested_asin</c> are both
/// missing, a seller reasonably concludes they need both.
/// </remarks>
public sealed class AmazonRequirementChoice
{
    /// <summary>The attributes that each satisfy this requirement on their own.</summary>
    public List<string> Options { get; set; } = [];

    /// <summary>The one that did, or empty when none did.</summary>
    public string SatisfiedBy { get; set; } = "";

    public bool Satisfied => SatisfiedBy.Length > 0;

    /// <summary>The requirement in a sentence, including what would satisfy it.</summary>
    public string Note { get; set; } = "";
}

/// <summary>
/// A whole eBay draft read onto one Amazon product type.
/// </summary>
public sealed class AmazonListingFill
{
    /// <summary>ok | not_configured | no_match | ambiguous | error | stale — <see cref="AmazonDefinitionStatus"/>.</summary>
    public string Status { get; set; } = AmazonDefinitionStatus.Ok;

    /// <summary>Why, when <see cref="Status"/> is not ok. Never a stack trace.</summary>
    public string Message { get; set; } = "";

    public string ProductType { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string MarketplaceId { get; set; } = "";
    public string Locale { get; set; } = "";
    public string Version { get; set; } = "";

    /// <summary>The eBay draft's title, so an answer can be told apart from another answer.</summary>
    public string SourceTitle { get; set; } = "";

    /// <summary>Set when the product type came from the sandbox's canned data. See AmazonSandboxNotice.</summary>
    public string SandboxNotice { get; set; } = "";

    /// <summary>Every attribute of the product type, required first, each with its verdict.</summary>
    public List<AmazonFilledAttribute> Attributes { get; set; } = [];

    /// <summary>Amazon's either/or requirements and whether the draft answers them.</summary>
    public List<AmazonRequirementChoice> Choices { get; set; } = [];

    /// <summary>
    /// The <c>attributes</c> object of a Listings Items submission, built from the filled attributes.
    /// </summary>
    /// <remarks>
    /// Present even when the listing cannot be submitted. A partial payload is the useful artefact —
    /// it is what shows a seller that nine of their eleven required fields are already answered and
    /// exactly which two are not.
    /// </remarks>
    public JsonObject Payload { get; set; } = [];

    public IEnumerable<AmazonFilledAttribute> Filled =>
        Attributes.Where(a => a.IsFilled);

    public IEnumerable<AmazonFilledAttribute> Blocking =>
        Attributes.Where(a => a.IsBlocking);

    /// <summary>Required attributes that are answered — the numerator a seller wants to see.</summary>
    public int RequiredFilledCount =>
        Attributes.Count(a => a.Required && a.IsFilled);

    public int RequiredCount => Attributes.Count(a => a.Required);

    /// <summary>
    /// True only when every required attribute is filled and every either/or requirement is met.
    /// </summary>
    /// <remarks>
    /// This is the whole point of the phase and the one field that must never be optimistic. It says
    /// nothing about whether Amazon will accept the listing — only Amazon validates — it says the app
    /// has no remaining reason to believe it will be rejected.
    /// </remarks>
    public bool CanSubmit =>
        Status is AmazonDefinitionStatus.Ok or AmazonDefinitionStatus.Stale &&
        !Attributes.Any(a => a.IsBlocking) &&
        Choices.All(c => c.Satisfied);

    /// <summary>One sentence for the top of the panel.</summary>
    public string Headline { get; set; } = "";
}
