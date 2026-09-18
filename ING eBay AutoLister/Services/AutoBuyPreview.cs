using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>Read-only search. Deliberately has no store, placer, or execution service.</summary>
public static class AutoBuyPreview
{
    public static AutoBuyRule Criteria(AutoBuyRuleRequest request)
    {
        var query = request.Query?.Trim() ?? "";
        if (query.Length == 0) throw new InvalidOperationException("Enter a product or keywords to search eBay.");
        if (query.Length > 300) throw new InvalidOperationException("Keep the search under 300 characters.");
        if (!Enum.TryParse<AutoBuyMode>(request.Mode ?? "BuyItNow", true, out var mode) || !Enum.IsDefined(mode))
            throw new InvalidOperationException("Choose Buy It Now, Best Offer, or Auction.");
        var max = request.MaxItemPrice.GetValueOrDefault();
        if (max < 0 || max > AutoBuyStore.MaxItemPriceCeiling)
            throw new InvalidOperationException("Enter an item price between $0 and $10,000.");
        if (request.MinItemPrice < 0 || request.MinItemPrice > (max == 0 ? AutoBuyStore.MaxItemPriceCeiling : max))
            throw new InvalidOperationException("Minimum price must be between zero and the maximum price.");
        var condition = request.Condition?.Trim().ToUpperInvariant() ?? "";
        if (condition is not ("" or "NEW" or "USED" or "REFURBISHED" or "FOR_PARTS"))
            throw new InvalidOperationException("Choose a condition from the list.");
        return new AutoBuyRule
        {
            Query = query, Mode = mode, Condition = condition,
            MaxItemPrice = max == 0 ? AutoBuyStore.MaxItemPriceCeiling : max,
            MinItemPrice = request.MinItemPrice ?? 0,
            RequiredKeywords = request.RequiredKeywords?.Trim() ?? "",
            MinSellerFeedback = Math.Max(0, request.MinSellerFeedback ?? 0),
            ExcludeKeywords = request.ExcludeKeywords?.Trim() ?? "",
            OfferOrBidPrice = Math.Max(0, request.OfferOrBidPrice ?? 0),
            Enabled = false,
        };
    }

    public static async Task<IReadOnlyList<EbayOpportunityItem>> SearchAsync(
        AutoBuyRuleRequest request, AutoBuyListingSource source, CancellationToken ct)
    {
        var rule = Criteria(request);
        var items = await source(rule, ct).WaitAsync(ct);
        // Do not apply budget/offer checks to browsing. Those belong to saving and executing rules.
        return items.Where(item => item.Price > 0 && item.Price <= rule.MaxItemPrice
            && item.Price >= rule.MinItemPrice
            && (!item.ShippingStated || item.Price + item.ShippingCost <= rule.MaxItemPrice)
            && AutoBuyService.ExcludeWords(rule.RequiredKeywords).All(word => item.Title.Contains(word, StringComparison.OrdinalIgnoreCase))
            && item.SellerFeedbackScore >= rule.MinSellerFeedback
            && !AutoBuyService.ExcludeWords(rule.ExcludeKeywords).Any(word =>
                item.Title.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(item => string.IsNullOrWhiteSpace(item.ItemId) ? item.Url : item.ItemId)
            .Take(50).ToList();
    }
}
