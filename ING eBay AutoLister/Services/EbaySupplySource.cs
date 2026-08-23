using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// eBay itself as a place to BUY, not just the yardstick everything else is measured against.
///
/// Every other source in this app exists to find something cheap and resell it on eBay, and eBay
/// sold prices are what decide whether a deal is a deal. That left the largest marketplace in the
/// app missing from the one screen that asks "where can I buy this" — which reads as an omission,
/// because it is one. Underpriced Buy It Nows and no-bid auctions are ordinary eBay-to-eBay flips,
/// and they are the supply a seller can act on without leaving the house.
///
/// This is a thin adapter, deliberately: <see cref="EbayService.SearchEndingSoonAsync"/> already
/// does the Browse API call, and everything downstream — grouping, comp lookup, ProfitCalculator,
/// ranking — is written against <see cref="LocalSupplyListing"/> and never learns which site a row
/// came from. The only real work here is mapping one shape to the other honestly.
///
/// Two things it says about itself that change what the UI promises:
///   • <see cref="IsLocationBased"/> is false. eBay ships nationwide, so the zip and radius on the
///     form mean nothing to it, and a scan that searched the whole country must not be reported as
///     "within 40 miles". Craigslist and Facebook are the location-based ones.
///   • It therefore charges sales tax by default (see ILocalSupplySource.ChargesSalesTax), which is
///     true — an eBay purchase is taxed, unlike cash to a stranger in a driveway.
///
/// Read-only against eBay: item_summary/search and nothing else. Nothing bids, buys or spends.
/// </summary>
/// <summary>
/// What to narrow an eBay supply scan to. Everything here maps to a filter the Browse API already
/// understands, so nothing in this record is a promise the search can't keep.
/// </summary>
/// <param name="Condition">NEW | USED | REFURBISHED | FOR_PARTS, or null for any.</param>
/// <param name="ListingType">AUCTION | FIXED_PRICE | BOTH.</param>
/// <param name="MinFeedback">Drop sellers below this feedback score. 0 keeps everyone.</param>
public record EbayScanFilters(
    string? Condition = null,
    string ListingType = "BOTH",
    decimal? MinPrice = null,
    decimal? MaxPrice = null,
    int MinFeedback = 0)
{
    public static readonly EbayScanFilters Default = new();

    /// <summary>
    /// Auctions are sorted soonest-ending because the whole premise is buying before it closes.
    /// Everything else uses eBay's Best Match.
    /// </summary>
    /// <remarks>
    /// This used to be cheapest-first, on the reasoning that an underpriced listing is at the bottom
    /// of that order. It is, and so is everything else. eBay carries 898,905 listings for "fanuc";
    /// asking for the 200 cheapest returns 200 repair shops advertising labour at $0.99, because a
    /// service listing has no cost of goods and can be priced at anything. Measured on the live
    /// Browse API, of the 50 cheapest "fanuc" listings <b>49</b> were services or manuals — the junk
    /// screen correctly binned them and the board came back empty. Under Best Match, <b>zero</b> of
    /// the same 50 were junk and the rows were real amplifiers and control modules at $258-$976.
    ///
    /// Cheapest-first is not how you find something underpriced; it is how you find whatever is
    /// cheapest to list. Finding the underpriced ones is what the profit ranking downstream is for,
    /// and it can only rank what the search actually returned.
    /// </remarks>
    public string Sort => ListingType == "AUCTION" ? "endingSoonest" : BestMatch;

    /// <summary>Sentinel meaning "send no sort parameter", which is how the Browse API selects Best Match.</summary>
    public const string BestMatch = "bestMatch";

    /// <summary>Plain-English summary of what was actually narrowed, for the UI to echo back.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        parts.Add(ListingType switch
        {
            "AUCTION"     => "auctions ending soonest",
            "FIXED_PRICE" => "Buy It Now, best match",
            _             => "auctions and Buy It Now, best match",
        });
        if (!string.IsNullOrWhiteSpace(Condition))
            parts.Add(Condition.Replace('_', ' ').ToLowerInvariant());
        if (MinPrice.HasValue || MaxPrice.HasValue)
            parts.Add($"{(MinPrice.HasValue ? $"${MinPrice:0.##}" : "any")}–{(MaxPrice.HasValue ? $"${MaxPrice:0.##}" : "any")}");
        if (MinFeedback > 0)
            parts.Add($"sellers with {MinFeedback}+ feedback");
        return string.Join(" · ", parts);
    }
}

public class EbaySupplySource(EbayService ebay, CredentialsStore creds, ActionLog log) : ILocalSupplySource
{
    public const string SourceId = "ebay";

    /// <summary>
    /// How many listings one Opportunity Finder search can pull back. Browse pages at 200 but
    /// exposes up to 10,000 results; EbayService follows every continuation page to this ceiling.
    /// </summary>
    /// <remarks>
    /// This was 50, which a cheapest-first sort turns into a hostage to whoever is flooding the
    /// bottom of the results. Searching "fanuc" returns 49 near-identical $0.99 "Repair Evaluation"
    /// ads from one repair shop in the first 50 rows: once <see cref="NonItemListingDetector"/>
    /// screens those, a 50-row page leaves a single real listing to price. The junk is a property
    /// of the cheap end of any parts search, so the page has to be deep enough to see past it.
    /// Nothing downstream gets more expensive — the scan still caps priced rows at its own maxItems.
    /// </remarks>
    // Historic name retained because the endpoint uses it as its clamp. It is now the complete
    // result-set limit, not one HTTP page; EbayService owns the real 200-row page size.
    public const int SearchPageSize = 10_000;

    private EbayScanFilters _filters = EbayScanFilters.Default;

    /// <summary>
    /// A copy of this source narrowed to <paramref name="filters"/>, for one scan.
    ///
    /// A copy rather than a setter because the registered instance is a singleton shared by the
    /// multi-source board: storing per-request filters on it would mean one seller's "used only"
    /// scan silently narrowing somebody else's — or, more likely here, narrowing the next scan the
    /// same person ran from the other panel.
    /// </summary>
    public EbaySupplySource WithFilters(EbayScanFilters filters)
    {
        var copy = new EbaySupplySource(ebay, creds, log);
        copy._filters = filters ?? EbayScanFilters.Default;
        return copy;
    }

    public string Id => SourceId;
    public string Label => "eBay";

    // The Browse search runs on an application token built from the seller's eBay app keys, not on
    // their user OAuth — so this is "configured", not "logged in". It still counts as a connection
    // because with no keys there is nothing to search with.
    public bool RequiresConnection => true;

    public bool IsAvailable
    {
        get
        {
            var c = creds.Get();
            return !string.IsNullOrWhiteSpace(c.EbayClientId) && !string.IsNullOrWhiteSpace(c.EbayClientSecret);
        }
    }

    public string AvailabilityNote => IsAvailable
        ? "Searches eBay nationwide — the zip and radius don't apply."
        : "Needs your eBay app keys (Settings) — the same ones that publish your listings.";

    // Nationwide. Saying so is what stops the panel promising a radius it never searched.
    public bool IsLocationBased => false;

    // A blank eBay search is "every listing on eBay", which is not a sourcing search.
    public bool AllowsBlankQuery => false;

    public async Task<LocalSupplySearchResult> SearchAsync(
        string query, string zip, int radiusMiles, CancellationToken ct = default)
    {
        var result = new LocalSupplySearchResult
        {
            SourceId    = SourceId,
            SourceLabel = Label,
            Query       = query ?? "",
            ZipCode     = zip ?? "",
            RadiusMiles = radiusMiles,
            SearchUrl   = BuildSearchUrl(query ?? ""),
            // Says what was actually narrowed, so a thin result reads as "you asked for used under
            // $200" rather than as eBay having nothing.
            ScopeLabel  = $"eBay nationwide — {_filters.Describe()}",
            Status      = "ok",
        };

        if (!IsAvailable)
        {
            result.Status = "not_connected";
            result.Error  = "Add your eBay app keys in Settings to search eBay for supply.";
            return result;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            result.Status = "error";
            result.Error  = "Enter something to search for.";
            return result;
        }

        List<EbayOpportunityItem> found;
        try
        {
            // Sort follows the listing type — see EbayScanFilters.Sort for why cheapest-first is
            // the right order for a supply search and soonest-ending is right for auctions.
            found = await ebay.SearchEndingSoonAsync(
                query, minFeedback: _filters.MinFeedback, limit: SearchPageSize, category: null,
                condition: _filters.Condition, minPrice: _filters.MinPrice, maxPrice: _filters.MaxPrice,
                listingType: _filters.ListingType, sortOverride: _filters.Sort);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Status    = "error";
            result.Error     = $"eBay search failed: {ex.Message}";
            result.Retryable = true;
            return result;
        }

        var items = found.Select(ToListing).ToList();
        items = LocalSupplyResults.FilterByRelevance(items, query);
        items = LocalSupplyResults.Dedupe(items);

        var (min, median, max) = LocalSupplyResults.Summarize(items);
        result.Items  = items;
        result.Min    = min;
        result.Median = median;
        result.Max    = max;

        log.Add("Info", "eBay supply search", $"\"{query}\" — {items.Count} listing(s) that could be bought and reflipped.");
        return result;
    }

    private LocalSupplyListing ToListing(EbayOpportunityItem item)
    {
        // Delivered price, not the headline price. What decides whether a flip works is what
        // actually leaves the wallet, and an item at $40 with $18 shipping is a $58 buy — booking
        // it as $40 makes a losing deal look like a winner.
        var shipping  = item.ShippingStated ? item.ShippingCost : 0m;
        var delivered = item.Price + shipping;

        var isAuction = item.BuyingOption.Contains("AUCTION", StringComparison.OrdinalIgnoreCase);

        return new LocalSupplyListing
        {
            Source      = SourceId,
            SourceLabel = Label,
            ItemId      = string.IsNullOrWhiteSpace(item.ItemId) ? item.Url : item.ItemId,
            Title       = item.Title,
            Url         = item.Url,
            ImageUrl    = item.ImageUrl,

            Price     = delivered > 0 ? delivered : null,
            PriceText = BuildPriceText(item, shipping, delivered),
            IsFree    = false,

            // eBay states a shipping cost of zero rather than leaving it unknown, which is the one
            // case where "free shipping" is a fact rather than an assumption.
            FreeShipping = item.ShippingStated && item.ShippingCost == 0m,

            // No geography: this row could be anywhere in the country, and pretending otherwise is
            // what IsLocationBased exists to prevent.
            Location      = "eBay",
            DistanceMiles = null,
            SellerUsername = item.SellerUsername,
            SellerFeedbackScore = item.SellerFeedbackScore,
            SellerFeedbackPercent = item.SellerFeedbackPercent,

            PostedAgo = BuildTimingText(item, isAuction),
        };
    }

    private static string BuildPriceText(EbayOpportunityItem item, decimal shipping, decimal delivered)
    {
        if (delivered <= 0) return "";
        if (!item.ShippingStated) return $"${item.Price:0.##} + shipping not stated";
        return shipping > 0
            ? $"${delivered:0.##} delivered (${item.Price:0.##} + ${shipping:0.##} ship)"
            : $"${delivered:0.##} delivered";
    }

    // An auction's remaining time is the difference between a deal and a deal someone else wins,
    // so it goes where every board already renders free text.
    private static string BuildTimingText(EbayOpportunityItem item, bool isAuction)
    {
        if (!isAuction) return "Buy It Now";

        var bids = item.BidCount == 1 ? "1 bid" : $"{item.BidCount} bids";
        if (item.EndDate is not { } end) return $"Auction · {bids}";

        var left = end.ToUniversalTime() - DateTime.UtcNow;
        if (left <= TimeSpan.Zero) return $"Auction ended · {bids}";

        var when = left.TotalHours < 1 ? $"{(int)left.TotalMinutes}m"
                 : left.TotalDays  < 1 ? $"{(int)left.TotalHours}h"
                 : $"{(int)left.TotalDays}d";
        return $"Ends in {when} · {bids}";
    }

    private static string BuildSearchUrl(string query) =>
        "https://www.ebay.com/sch/i.html?_nkw=" + Uri.EscapeDataString(query) + "&_sop=15";
}
