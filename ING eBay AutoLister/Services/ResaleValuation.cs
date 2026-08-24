using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>The ids the catalog points at, so a category and its provider can't drift apart.</summary>
public static class ResaleValuationProviders
{
    public const string EbayComps = "ebay_comps";
    public const string EbayMotors = "ebay_motors";
    public const string BulkyLocal = "bulky_local";
    /// <summary>Not a provider in the list — the third tier, which answers before they are asked.</summary>
    public const string AiEstimate = "ai_estimate";
    /// <summary>
    /// Also not a provider in the list: the spot price of the metal a lot states it contains. It
    /// overrules the providers rather than joining them, because it does not depend on a comps
    /// lookup being right — see <see cref="MeltAnchor"/>.
    /// </summary>
    public const string MetalMelt = "metal_melt";
}

/// <summary>A valuation: the price, when there is one, and always the sentence explaining it.</summary>
/// <param name="Pricing">
/// Null when the provider refused. That null is the point of this whole file — it is what stops a
/// row publishing the median of four tow hitches as the resale value of a truck.
/// </param>
public sealed record ValuationOutcome(ResaleValuation Valuation, ResalePricing? Pricing);

/// <summary>
/// One way of answering "what does this resell for", for the categories it understands.
/// </summary>
/// <remarks>
/// <para>
/// The sold-comps lookup itself is shared and runs once per product upstream (see
/// FindLocalArbitrageAsync) — a provider is not a second database, it is the judgement about
/// whether that lookup's answer means anything for THIS kind of thing, and what to say when it
/// doesn't. That split is deliberate: it keeps the expensive part rationed in one place and makes
/// every provider pure, total and testable.
/// </para>
/// <para>
/// A real eBay Motors feed, a Kelley Blue Book key or a boat-price service would drop in here as a
/// fourth implementation without any other file changing.
/// </para>
/// </remarks>
public interface IResaleValuationProvider
{
    string Id { get; }
    /// <summary>How the row names this source: "eBay sold comps", "eBay Motors sold comps".</summary>
    string Label { get; }
    bool Handles(ResaleCategory category);
    ValuationOutcome Value(ResaleCategory category, LocalSupplyListing listing, ResalePricing? comps);
}

/// <summary>
/// The original behaviour, unchanged: the hosted sold-comps database and Terapeak, taken at their
/// word. Right for everything the database is actually full of — electronics, tools, parts, the
/// small parcel goods this app was built around.
/// </summary>
public sealed class EbayCompsValuationProvider : IResaleValuationProvider
{
    public string Id => ResaleValuationProviders.EbayComps;
    public string Label => "eBay sold comps";

    public bool Handles(ResaleCategory category) => category.ValuationProviderId == Id;

    public ValuationOutcome Value(ResaleCategory category, LocalSupplyListing listing, ResalePricing? comps)
    {
        if (comps is null || !comps.HasPrice)
        {
            return new ValuationOutcome(new ResaleValuation
            {
                Status = ValuationStatuses.Manual,
                ProviderId = Id,
                SourceLabel = "no sold history",
                Note = "No eBay sold history matched this title.",
                LookupQuery = listing.Title,
                LookupUrl = ResaleValuationLinks.SoldSearchUrl(category, listing.Title),
                // The lookup itself is passed through even though it found no price. It carries the
                // comp counts and the identity flag the row grades its (absent) evidence from, and
                // "we looked and found nothing" is a different row from "we never looked".
            }, comps);
        }

        return new ValuationOutcome(new ResaleValuation
        {
            Status = ValuationStatuses.Comps,
            ProviderId = Id,
            SourceLabel = Label,
            Note = $"Priced against eBay sold comps for \"{comps.LookupTitle}\".",
            LookupQuery = comps.LookupTitle,
            LookupUrl = ResaleValuationLinks.SoldSearchUrl(category, comps.LookupTitle),
        }, comps);
    }
}

/// <summary>
/// Comps that have to earn the right to price a big-ticket item, and an honest refusal when they
/// don't.
/// </summary>
/// <remarks>
/// <para>
/// The failure this exists to stop is not thin evidence — the board already grades that. It is
/// evidence about a <b>different kind of thing</b>. Ask a comps database built on electronics what
/// a 2011 Tundra sells for and it will find tow hitches, tail lights and floor mats, agree with
/// itself across a dozen of them, and hand back a confident $180. Run that through the profit math
/// against an $8,500 ask and the row says "pass" on the best flip on the board; run the mirror case
/// — a $600 project boat matched to a $9,000 comp — and it says "goldmine" on a wreck.
/// </para>
/// <para>
/// So the comps are checked against the ask before they are allowed to be a price. A titled thing
/// whose sold history sits at a small fraction of what it is being sold for locally is a parts
/// match, and a multiple far beyond what any local flip returns is a different vehicle. Either way
/// the answer is the same one a person would give: <b>I can't price this one — here is the search,
/// go and look.</b> Never a number.
/// </para>
/// </remarks>
public class GuardedCompsValuationProvider : IResaleValuationProvider
{
    private readonly string _id;
    private readonly string _label;
    // Two forms of the same word, because these sentences reach the seller verbatim and "that isn't
    // a cheap a vehicle" is the kind of thing that makes a real warning read as a bug.
    private readonly string _kindPhrase;   // "a vehicle" — reads after a preposition
    private readonly string _kindNoun;     // "vehicle"   — reads after an article or before "'s"

    /// <summary>Comps that priced it, below which nothing about a four-figure buy is knowable.</summary>
    public int MinComps { get; init; } = 3;
    /// <summary>Below ask × this, the comps are for parts rather than for the thing.</summary>
    public decimal MinResaleToAskRatio { get; init; } = 0.4m;
    /// <summary>Above ask × this, the comps are for a different (better) example of the thing.</summary>
    public decimal MaxResaleToAskRatio { get; init; } = 5m;
    /// <summary>Asks below this are too cheap for the ratio test to say anything — a $60 row is noise either way.</summary>
    public decimal AskFloor { get; init; } = 500m;

    public GuardedCompsValuationProvider(string id, string label, string kindPhrase, string kindNoun)
    {
        _id = id;
        _label = label;
        _kindPhrase = kindPhrase;
        _kindNoun = kindNoun;
    }

    public string Id => _id;
    public string Label => _label;

    public bool Handles(ResaleCategory category) => category.ValuationProviderId == Id;

    public ValuationOutcome Value(ResaleCategory category, LocalSupplyListing listing, ResalePricing? comps)
    {
        var query = VehicleTitleParser.SearchQuery(listing.Vehicle, comps?.LookupTitle ?? listing.Title);
        var url = ResaleValuationLinks.SoldSearchUrl(category, query);

        ValuationOutcome Manual(string note) => new(new ResaleValuation
        {
            Status = ValuationStatuses.Manual,
            ProviderId = Id,
            SourceLabel = "estimate unavailable",
            Confidence = LocalArbitrageEvidence.None,
            Note = note,
            LookupQuery = query,
            LookupUrl = url,
        }, null);

        if (comps is null || !comps.HasPrice)
        {
            return Manual($"No sold history this app can price {_kindPhrase} from — check the sold listings yourself " +
                          "before you go and look at it.");
        }

        var reason = Reject(category, listing, comps);
        if (reason is not null) return Manual(reason);

        return new ValuationOutcome(new ResaleValuation
        {
            Status = ValuationStatuses.Comps,
            ProviderId = Id,
            SourceLabel = Label,
            Note = $"Priced against {comps.EvidenceCompCount} sold comp{(comps.EvidenceCompCount == 1 ? "" : "s")} " +
                   $"for \"{comps.LookupTitle}\", checked against the local ask so a parts match can't set the price.",
            LookupQuery = query,
            LookupUrl = url,
        }, comps);
    }

    /// <summary>The reason these comps must not price this row, or null when they may.</summary>
    /// <remarks>Pure and total — every branch returns a sentence in this row's own numbers.</remarks>
    public string? Reject(ResaleCategory category, LocalSupplyListing listing, ResalePricing comps)
    {
        var evidence = comps.EvidenceCompCount;

        // A big-ticket ad with no price on it, which classifieds are full of — and which the parser
        // reasonably reads as "free" when the title happens to contain the word (a dealer ad
        // shouting "FREE SHIPPING!" is the common one). A $0 cost basis makes ROI unbounded, so a
        // van nobody priced would clear the goldmine bar on return alone. Nobody gives away a
        // vehicle; the ad simply didn't say. A genuine giveaway comes through the freebie board,
        // which states its own cost basis and is left alone here.
        if (listing.Freebie is null && listing.Price is null or <= 0m)
        {
            return $"This ad doesn't state a price. There's nothing to measure a resale value against " +
                   $"on {_kindPhrase}, so check the sold listings and ask the seller what they want.";
        }

        // Nothing carries this item's model or part number. On a $60 gadget that is a warning; on a
        // titled buy it means the price is for something else entirely, and there is no version of
        // "estimate" worth putting next to four figures.
        if (!comps.IdentityVerified)
        {
            return $"The sold comps don't carry this {_kindNoun}'s own model — they're for something else with the " +
                   "same badge on it. Price this one yourself.";
        }

        if (evidence < MinComps)
        {
            return $"Only {evidence} sold comp{(evidence == 1 ? "" : "s")} matched, which is nothing to price " +
                   $"{_kindPhrase} from. Check the sold listings yourself.";
        }

        var ask = listing.Price ?? 0m;
        var resale = comps.ExpectedSale is > 0 ? comps.ExpectedSale!.Value : comps.Median ?? 0m;
        if (ask < AskFloor || resale <= 0m) return null;

        if (resale < ask * MinResaleToAskRatio)
        {
            return $"The sold comps come out at {resale:C0} against a {ask:C0} ask — that isn't a cheap " +
                   $"{_kindNoun}, it's the price of parts for one. Nothing here can value it, so price it yourself.";
        }

        if (resale > ask * MaxResaleToAskRatio)
        {
            return $"The sold comps come out at {resale:C0} against a {ask:C0} ask. A {MaxResaleToAskRatio:0}× " +
                   $"return on {_kindPhrase} is a comp for a different one, not a deal — check the sold listings yourself.";
        }

        return null;
    }
}

/// <summary>The prefilled searches a refused row hands back. Links only — nothing is fetched.</summary>
public static class ResaleValuationLinks
{
    /// <summary>
    /// eBay's own sold-and-completed search, narrowed to the category's corner of the site where
    /// there is one. This is what a manual valuation actually IS: the seller running the search the
    /// app couldn't answer, against the identity the app did manage to read off the title.
    /// </summary>
    public static string SoldSearchUrl(ResaleCategory category, string? query)
    {
        var q = Uri.EscapeDataString((query ?? "").Trim());
        var url = $"https://www.ebay.com/sch/i.html?_nkw={q}&LH_Sold=1&LH_Complete=1";
        return category.EbaySoldCategoryId.Length > 0 ? $"{url}&_sacat={category.EbaySoldCategoryId}" : url;
    }
}

/// <summary>
/// Picks the provider that owns a category. Pluggable through DI — every provider is registered as
/// <see cref="IResaleValuationProvider"/> in Program.cs — with a static default so the pure paths
/// (the analyzer's own tests, anything constructing it by hand) don't need a container.
/// </summary>
public sealed class ResaleValuationRegistry(IEnumerable<IResaleValuationProvider> providers)
{
    public IReadOnlyList<IResaleValuationProvider> All { get; } = providers.ToList();

    public static ResaleValuationRegistry Default { get; } = new(BuildDefaults());

    /// <summary>
    /// The three shipped providers. Kept as a factory rather than a shared list so the DI
    /// registration and the static default can never be two different sets of rules.
    /// </summary>
    public static IReadOnlyList<IResaleValuationProvider> BuildDefaults() =>
    [
        new EbayCompsValuationProvider(),
        // Titled goods: the strictest guard in the app, because these are the rows with four
        // figures on them and the ones the comps database knows least about.
        new GuardedCompsValuationProvider(
            ResaleValuationProviders.EbayMotors, "eBay Motors sold comps", "a vehicle", "vehicle")
        {
            MinComps = 3, MinResaleToAskRatio = 0.4m, MaxResaleToAskRatio = 5m, AskFloor = 500m,
        },
        // Big things that do sell on eBay but are collected: appliances, furniture, tractors. Same
        // shape of check, looser bounds — a $150 dresser reselling for $600 is an ordinary flip,
        // where the same multiple on a truck would be a comp for a different truck.
        new GuardedCompsValuationProvider(
            ResaleValuationProviders.BulkyLocal, "eBay sold comps (checked against the local ask)",
            "something this size", "item")
        {
            MinComps = 3, MinResaleToAskRatio = 0.25m, MaxResaleToAskRatio = 8m, AskFloor = 150m,
        },
    ];

    public ValuationOutcome Value(ResaleCategory category, LocalSupplyListing listing, ResalePricing? comps)
    {
        // ── The model's estimate goes past the providers, not through them ───────────────────────
        // Every provider here exists to answer one question: does this sold-comps lookup describe
        // THIS kind of thing? A model estimate has no lookup behind it — it was asked about this
        // exact item, by name, precisely because no comp matched — so there is nothing for a guard
        // to check and a guard that ran would refuse the one price the row has. The vehicle and
        // bulky guards are the reason this matters: a truck and a chest freezer are exactly the
        // rows that reach the third tier, and they are the rows worth the most money.
        //
        // It is labelled as what it is, and its status is its own — never Comps — so nothing
        // downstream can mistake it for sold history.
        if (comps is { IsAiEstimate: true, HasPrice: true })
        {
            return new ValuationOutcome(new ResaleValuation
            {
                Status = ValuationStatuses.AiEstimate,
                ProviderId = ResaleValuationProviders.AiEstimate,
                SourceLabel = "AI estimate",
                Note = string.IsNullOrWhiteSpace(comps.AiBasis)
                    ? "No sold comp matched this item, so the model priced it against the wider resale market."
                    : $"No sold comp matched this item. The model priced it as {comps.AiBasis}, against the wider resale market.",
                LookupQuery = listing.Title,
                // The search link stays. An estimate the seller can check by hand in one click is a
                // different thing from one they have to take on faith.
                LookupUrl = ResaleValuationLinks.SoldSearchUrl(category, listing.Title),
                Confidence = LocalArbitrageEvidence.Ai,
            }, comps);
        }

        var provider = All.FirstOrDefault(p => p.Handles(category))
                    ?? All.FirstOrDefault(p => p.Id == ResaleValuationProviders.EbayComps);

        // No provider at all can only mean a misconfigured container. Refusing is the safe answer:
        // the row keeps its listing and its ask, and says it wasn't valued.
        return provider?.Value(category, listing, comps)
            ?? new ValuationOutcome(new ResaleValuation
            {
                Status = ValuationStatuses.Manual,
                SourceLabel = "estimate unavailable",
                Note = "No valuation source is configured for this category.",
                LookupQuery = listing.Title,
                LookupUrl = ResaleValuationLinks.SoldSearchUrl(category, listing.Title),
            }, null);
    }
}
