namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The things a hosted deployment has exactly one of, and what a signed-in account is and is not
/// allowed to do to them or be told about them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PerUserData"/> and <see cref="PerUserCredentials"/> split what is each seller's own.
/// What is left over is the deployment's: the saved Facebook and Terapeak browser sessions, the
/// folder the data lives in, the owner's licence key and eBay developer id. On the desktop build
/// all of that is the seller's too, because the seller is the owner. On a server it is not — and
/// the endpoints that touch it were written when there was no difference.
/// </para>
/// <para>
/// Every method here is a no-op in the desktop build. Which build is running is passed as a plain
/// <c>bool</c>, the same way <see cref="HostedAuth"/> takes it, so the tests drive both answers.
/// </para>
/// </remarks>
public static class HostedShared
{
    /// <summary>True on a hosted deployment. <paramref name="hosted"/> overrides the build; tests pass both.</summary>
    public static bool IsHosted(bool? hosted = null) => hosted ?? HostedAuth.IsHostedBuild;

    /// <summary>
    /// The answer to an account trying to connect, disconnect or drive a login that belongs to the
    /// whole deployment, or null when this is the desktop build and the login is the seller's own.
    /// </summary>
    /// <param name="what">The connection, as the seller knows it — "Facebook Marketplace", "Terapeak".</param>
    /// <remarks>
    /// 403 rather than a quiet 200: the caller asked for something to change and it did not.
    /// <c>started</c>, <c>connected</c> and <c>message</c> ride along because those are the fields
    /// the Settings cards already read from these endpoints.
    /// </remarks>
    public static IResult? Refusal(string what, bool? hosted = null)
    {
        if (!IsHosted(hosted)) return null;

        var sentence = $"{what} is connected once for everyone on the web version, so it can't be changed from an account here.";
        return Results.Json(new
        {
            error = sentence,
            message = sentence,
            detail = "Nothing was changed. In the Windows desktop app this connection is your own and these buttons work.",
            started = false,
            shared = true,
        }, statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// A path on the machine the app runs on: the seller's own folder on the desktop build, and
    /// nothing on a server, where it is the host's filesystem layout and no account's business.
    /// </summary>
    public static string ServerPath(string? path, bool? hosted = null) => IsHosted(hosted) ? "" : path ?? "";

    /// <summary>
    /// The Settings fields with the deployment's own values taken out on a server.
    /// </summary>
    /// <remarks>
    /// On a hosted deployment the eBay developer id and the licence key come from the owner's
    /// configuration and are laid over every account's record (<see cref="ServerCredentials"/>), so
    /// what the Settings screen would show a new sign-up is the owner's. Neither is needed by the
    /// page: both are keep-if-blank on save, and the server puts its own values back on every read.
    /// The Client ID and RuName stay — they are in every eBay consent URL the seller is sent to.
    /// </remarks>
    public static PublicFields WithoutDeploymentValues(PublicFields fields, bool? hosted = null)
    {
        if (!IsHosted(hosted)) return fields;

        fields.EbayDevId = "";
        fields.LicenseKeyPreview = "";
        return fields;
    }
}
