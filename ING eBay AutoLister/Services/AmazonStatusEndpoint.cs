using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// <c>/api/amazon/status</c> — the Amazon counterpart of <c>/api/ebay/status</c>.
/// </summary>
/// <remarks>
/// <para>
/// Kept out of Program.cs as one small unit, for the reason <see cref="CalibrationEndpoints"/> is:
/// the handler the tests exercise is then the handler the app actually serves. A diagnostic whose
/// test asserts against a copy of itself proves nothing about the answer an operator gets.
/// </para>
/// <para>
/// Unlike the eBay status endpoint this one can spend a network call — but only when the
/// configuration could support a token at all. The state this deployment is in today (the owner's
/// sandbox application, no seller authorisation) is answered instantly and offline, which is what
/// makes it safe to poll.
/// </para>
/// <para>
/// <b>What it never carries:</b> the access token, any of the five credentials, or Amazon's response
/// body. Booleans, hostnames, Amazon's own error code and this app's own sentences — the same rule
/// <c>/api/ebay/status</c> follows, and <c>PublishSecretsTests</c> is the reason it is worth stating.
/// </para>
/// </remarks>
public static class AmazonStatusEndpoint
{
    public const string Path = "/api/amazon/status";

    public static void Map(WebApplication app)
    {
        app.MapGet(Path, async (AmazonService amazon, CancellationToken cancellationToken) =>
        {
            var status = await amazon.GetStatusAsync(cancellationToken);
            var o = amazon.Options;

            return Results.Ok(new
            {
                configured            = status.Configured,
                applicationConfigured = status.ApplicationConfigured,
                tokenObtainable       = status.TokenObtainable,
                sandbox               = o.Sandbox,
                environment           = status.Environment,
                region                = status.Region,
                apiHost               = status.ApiHost,
                tokenEndpoint         = status.TokenEndpoint,
                code                  = status.Code,
                message               = status.Message,
                nextAction            = status.NextAction,
                tokenExpiresAt        = status.TokenExpiresAt?.ToString("u"),
                httpStatus            = status.HttpStatus,
                // Which pieces are present — never their values. This is the part an operator
                // actually needs: "configured: false" that does not say which of the five is
                // missing is a scavenger hunt through someone else's environment file.
                has = new
                {
                    clientId      = !string.IsNullOrWhiteSpace(o.ClientId),
                    clientSecret  = !string.IsNullOrWhiteSpace(o.ClientSecret),
                    refreshToken  = !string.IsNullOrWhiteSpace(o.RefreshToken),
                    marketplaceId = !string.IsNullOrWhiteSpace(o.MarketplaceId),
                    sellerId      = !string.IsNullOrWhiteSpace(o.SellerId),
                },
            });
        });
    }
}
