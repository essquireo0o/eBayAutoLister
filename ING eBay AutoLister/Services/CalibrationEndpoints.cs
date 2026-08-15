using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The two admin endpoints the arb-bot and the owner use to write and read the learned pricing
/// calibration. Kept out of Program.cs as one small unit so the same handlers the app maps are the
/// ones the tests exercise.
/// </summary>
/// <remarks>
/// Both are gated by the admin key exactly as <c>/api/owner/stats</c> is — <see cref="CredentialsStore.EnsureAdminKey"/>
/// then a constant-time <see cref="CredentialsStore.AdminKeyMatches"/> — and both carry the same
/// auth-style rate limit. Neither throws to the client: a bad body is a 400, an unexpected fault is a
/// logged 500, because this feeds live pricing and the safe answer to "I couldn't understand that"
/// is to keep the last good calibration.
/// </remarks>
public static class CalibrationEndpoints
{
    public const string UpdatePath = "/api/calibration/update";
    public const string ReadPath = "/api/calibration";

    public static void Map(WebApplication app)
    {
        // The arb-bot POSTs its freshly-measured calibration here every few hours. Admin-key gated,
        // never signed in — the bot is a headless process holding the deployment's admin key.
        app.MapPost(UpdatePath, async (string? k, HttpRequest request,
            CredentialsStore store, CalibrationStore calibration, ActionLog log) =>
        {
            store.EnsureAdminKey();
            if (!store.AdminKeyMatches(k)) return Results.Unauthorized();

            try
            {
                using var reader = new StreamReader(request.Body);
                var body = await reader.ReadToEndAsync();

                var data = CalibrationStore.Parse(body);
                if (data is null)
                    return Results.BadRequest(new { ok = false, error = "Calibration body was not valid JSON." });

                var stored = calibration.Save(data);
                log.Add("Info", "Pricing calibration updated",
                    $"{stored.SampleSize} holdout(s), overall bias {stored.OverallBiasPct:0.0}% "
                    + $"({stored.Buckets.Count} bucket(s)), predictor \"{stored.Predictor}\".");

                return Results.Ok(new { ok = true, sampleSize = stored.SampleSize, buckets = stored.Buckets.Count });
            }
            catch (Exception ex)
            {
                // Never throw to the client. The last good calibration stays in force.
                log.Add("Warning", "Pricing calibration update failed", ex.Message);
                return Results.Json(new { ok = false, error = "Calibration could not be stored." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        // AllowAnonymous, because the caller is the headless arb-bot, not a signed-in browser: the
        // admin key IS the credential here (constant-time AdminKeyMatches above), the same way a
        // webhook authenticates by a shared secret rather than a session. Without this the hosted
        // build's closed-by-default sign-in policy answers the bot with 401 before the key is even
        // read. The write is low-harm regardless — a bounded, ±20%-clamped pricing nudge behind a
        // kill switch (Calibration:Enabled).
        }).AllowAnonymous().RateLimitLikeAuth();

        // Read the current calibration back — for the owner, or the bot confirming its last write.
        app.MapGet(ReadPath, (string? k, CredentialsStore store, CalibrationStore calibration) =>
        {
            store.EnsureAdminKey();
            if (!store.AdminKeyMatches(k)) return Results.Unauthorized();
            return Results.Ok(calibration.Current);
        }).AllowAnonymous().RateLimitLikeAuth();
    }
}
