using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

public class AutoBuyPreviewTests
{
    [Fact]
    public async Task Search_needs_no_budget_or_offer_and_passes_criteria_to_source()
    {
        AutoBuyRule? received = null;
        var rows = await AutoBuyPreview.SearchAsync(new() { Query = " Latitude 5400 ", Mode = "BestOffer", Condition = "used" },
            (rule, ct) => { received = rule; return Task.FromResult<IReadOnlyList<EbayOpportunityItem>>([]); }, default);
        Assert.Empty(rows);
        Assert.NotNull(received);
        Assert.Equal("Latitude 5400", received.Query);
        Assert.Equal(AutoBuyMode.BestOffer, received.Mode);
        Assert.Equal("USED", received.Condition);
        Assert.False(received.Enabled);
        Assert.Equal(10_000m, received.MaxItemPrice);
    }

    [Fact]
    public async Task Search_excludes_nonmatching_prices_feedback_and_titles_and_deduplicates()
    {
        EbayOpportunityItem Row(string id, string title = "Laptop", decimal price = 80, int feedback = 100) =>
            new() { ItemId = id, Title = title, Price = price, SellerFeedbackScore = feedback };
        var rows = await AutoBuyPreview.SearchAsync(new() { Query = "Laptop", MaxItemPrice = 100, MinSellerFeedback = 50, ExcludeKeywords = "parts, broken" },
            (_, _) => Task.FromResult<IReadOnlyList<EbayOpportunityItem>>([
                Row("1"), Row("1"), Row("2", price:101), Row("3", feedback:20), Row("4", "BROKEN laptop"), Row("5", price:0)]), default);
        Assert.Single(rows);
        Assert.Equal("1", rows[0].ItemId);
    }

    [Theory]
    [InlineData("", "BuyItNow", 100)]
    [InlineData("laptop", "unknown", 100)]
    [InlineData("laptop", "99", 100)]
    [InlineData("laptop", "BuyItNow", -1)]
    [InlineData("laptop", "BuyItNow", 10001)]
    public async Task Invalid_input_never_calls_ebay(string query, string mode, int max)
    {
        var called = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => AutoBuyPreview.SearchAsync(
            new() { Query = query, Mode = mode, MaxItemPrice = max },
            (_, _) => { called = true; return Task.FromResult<IReadOnlyList<EbayOpportunityItem>>([]); }, default));
        Assert.False(called);
    }
}
