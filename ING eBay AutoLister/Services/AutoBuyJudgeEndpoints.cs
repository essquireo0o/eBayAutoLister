using ING_eBay_AutoLister.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// <c>/api/autobuy/judge</c> — "is this listing actually a deal?" Prices one candidate against real
/// eBay sold comps, reads its title for trouble words, and returns Buy / Caution / Skip with the
/// numbers behind it. Read-only: it never saves a rule, and never places an order, bid, or offer.
/// </summary>
public static class AutoBuyJudgeEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/autobuy/judge", async (AutoBuyJudgeRequest req, AutoBuyDealJudge judge, CancellationToken ct) =>
        {
            if (req is null || req.Price <= 0m)
                return Results.BadRequest(new { ok = false, error = "A listing price above zero is required." });

            var item = new EbayOpportunityItem
            {
                Title = req.Title ?? "",
                Price = req.Price,
                Condition = req.Condition ?? "",
                ItemId = req.ItemId ?? "",
                Url = req.Url ?? "",
            };
            var minProfit = req.MinNetProfit ?? AutoBuyDealJudge.DefaultMinNetProfit;
            var minRoi = req.MinRoiPercent ?? AutoBuyDealJudge.DefaultMinRoiPercent;

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                var judgment = await judge.JudgeAsync(item, req.Query, minProfit, minRoi, timeout.Token);
                return Results.Ok(new { ok = true, judgment });
            }
            catch (OperationCanceledException)
            {
                return Results.Json(new { ok = false, error = "The sold-comps lookup timed out. Try again." }, statusCode: 504);
            }
        });
    }
}

/// <summary>What the judge needs to weigh one listing. Only the price is required.</summary>
public sealed class AutoBuyJudgeRequest
{
    public string? Title { get; set; }
    public decimal Price { get; set; }
    public string? Condition { get; set; }
    public string? ItemId { get; set; }
    public string? Url { get; set; }
    /// <summary>The rule's search, so comps line up with what it hunts. Falls back to the title.</summary>
    public string? Query { get; set; }
    public decimal? MinNetProfit { get; set; }
    public decimal? MinRoiPercent { get; set; }
}
