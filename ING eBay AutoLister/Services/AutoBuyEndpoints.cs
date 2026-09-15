using ING_eBay_AutoLister.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The routes behind the eBay Auto-Buy console. Bookkeeping over <see cref="AutoBuyStore"/> plus one
/// call into <see cref="AutoBuyService"/> for a "Run now", so the button and the timer take exactly
/// the same path — the same matching, the same caps, the same arm.
/// </summary>
/// <remarks>
/// Nothing here spends money on a GET, and the status route is what the open console polls. Arming,
/// live buying and the caps all go through <see cref="AutoBuyStore.SaveSettings"/>; a body that
/// names one switch flips only that one, so turning live buying off never disarms the master by
/// accident, and vice versa.
/// </remarks>
public static class AutoBuyEndpoints
{
    public static void Map(WebApplication app)
    {
        // The whole feature in one shape, polled by the open console.
        app.MapGet("/api/autobuy/status", (AutoBuyService service) =>
            Results.Ok(service.BuildStatus()));

        app.MapGet("/api/autobuy/executions", (int? limit, AutoBuyStore store) =>
            Results.Ok(store.ListRecent(limit ?? 60)));

        // Create or edit one rule. A partial body edits only what it names.
        app.MapPost("/api/autobuy/rules", (AutoBuyRuleRequest req, AutoBuyStore store, AutoBuyService service, ActionLog log) =>
        {
            try
            {
                var rule = store.SaveRule(req);
                log.Add("Auto-Buy", req.Id is > 0 ? "Rule updated" : "Rule saved",
                    $"\"{rule.Name}\" — {AutoBuyService.ModeVerb(rule.Mode)} up to {rule.MaxItemPrice:C2}, budget {rule.BudgetCap:C2}.");
                return Results.Ok(new { ok = true, rule, status = service.BuildStatus() });
            }
            catch (InvalidOperationException ex)
            {
                // Validation messages are sentences the seller can act on; return them as-is.
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
        });

        app.MapDelete("/api/autobuy/rules/{id:long}", (long id, AutoBuyStore store, AutoBuyService service, ActionLog log) =>
        {
            var deleted = store.DeleteRule(id);
            if (deleted) log.Add("Auto-Buy", "Rule deleted", $"Rule #{id} was removed.");
            return Results.Ok(new { ok = deleted, status = service.BuildStatus() });
        });

        // The master switches and the global caps. Arming and turning on live buying are two
        // separate acts on purpose — see AutoBuySettings.
        app.MapPost("/api/autobuy/settings", (AutoBuySettingsRequest req, AutoBuyStore store, AutoBuyService service, ActionLog log) =>
        {
            var before = store.GetSettings();
            var after = store.SaveSettings(req.Armed, req.LiveBuying, req.GlobalBudgetCap, req.MaxBuysPerDay);

            if (before.Armed != after.Armed)
                log.Add("Auto-Buy", after.Armed ? "Armed" : "Disarmed",
                    after.Armed ? "Auto-Buy will now scan your rules." : "Auto-Buy stopped. Nothing scans or buys.");
            if (before.LiveBuying != after.LiveBuying)
                log.Add("Auto-Buy", after.LiveBuying ? "LIVE BUYING ON" : "Live buying off",
                    after.LiveBuying ? "Rules can now spend real money inside their caps." : "Back to simulation — nothing is bought.");

            return Results.Ok(new { ok = true, status = service.BuildStatus() });
        });

        // Scan one rule now. Takes the same one-at-a-time gate the timer does.
        app.MapPost("/api/autobuy/rules/{id:long}/run", async (long id, AutoBuyService service, CancellationToken ct) =>
        {
            var run = await service.RunRuleAsync(id, manual: true, ct);
            return Results.Ok(new { ok = true, run, status = service.BuildStatus() });
        });
    }
}

/// <summary>The console's settings post. Every field optional so one switch can be flipped alone.</summary>
public sealed class AutoBuySettingsRequest
{
    public bool? Armed { get; set; }
    public bool? LiveBuying { get; set; }
    public decimal? GlobalBudgetCap { get; set; }
    public int? MaxBuysPerDay { get; set; }
}
