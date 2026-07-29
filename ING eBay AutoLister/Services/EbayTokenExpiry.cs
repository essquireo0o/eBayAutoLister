namespace ING_eBay_AutoLister.Services;

/// <summary>
/// When an eBay token is too old to use, and when the app should go and get a new one.
/// </summary>
/// <remarks>
/// <para>
/// Pulled out of <see cref="CredentialsStore"/> and given an explicit <c>now</c> so the arithmetic
/// can be tested at all — it used to read <see cref="DateTimeOffset.UtcNow"/> inside the store,
/// which made "does the refresh fire before expiry" a question nothing could answer except a
/// two-hour wait.
/// </para>
/// <para>
/// The rule that matters is what an <i>unknown</i> expiry means. A stored access token with no
/// recorded expiry used to count as fresh forever, so the proactive refresh never ran for it and
/// the first sign it was dead was a 401 in the middle of publishing. Unknown now means "refresh it
/// if there is anything to refresh with", and only falls back to using it as-is when there is no
/// refresh token — which is the hand-pasted-token case, where a stale token is still strictly
/// better than refusing to try.
/// </para>
/// </remarks>
public static class EbayTokenExpiry
{
    /// <summary>Treat a token as expired this long before eBay does, to cover a slow request.</summary>
    public static readonly TimeSpan AccessTokenSkew = TimeSpan.FromSeconds(90);

    /// <summary>How far ahead of expiry the background loop tops the access token up.</summary>
    public static readonly TimeSpan DefaultRefreshLead = TimeSpan.FromMinutes(20);

    /// <summary>A refresh token inside its last day is treated as spent; eBay will not renew it.</summary>
    public static readonly TimeSpan RefreshTokenSafetyMargin = TimeSpan.FromDays(1);

    /// <summary>
    /// True when the stored access token must not be used as-is. <paramref name="hasRefreshToken"/>
    /// decides the unknown-expiry case: refreshable means refresh, otherwise use what we have.
    /// </summary>
    public static bool IsAccessTokenExpired(
        string? accessToken, DateTimeOffset? expiresAt, bool hasRefreshToken, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return true;
        if (expiresAt is null) return hasRefreshToken;
        return now >= expiresAt.Value - AccessTokenSkew;
    }

    /// <summary>
    /// True when there is a refresh token worth spending a request on. A missing expiry is trusted
    /// here — eBay only reports <c>refresh_token_expires_in</c> on the initial exchange, so a
    /// connection made before that was recorded has no date and is not thereby dead.
    /// </summary>
    public static bool IsRefreshTokenUsable(string? refreshToken, DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return false;
        if (expiresAt is null) return true;
        return now < expiresAt.Value - RefreshTokenSafetyMargin;
    }

    /// <summary>
    /// True when the background loop should refresh right now: there is a usable refresh token, and
    /// the access token is missing, of unknown age, or inside <paramref name="lead"/> of expiry.
    /// </summary>
    /// <remarks>
    /// The missing-access-token case is the one a restart lands on — the refresh token survives in
    /// credentials.json and the access token has long since expired — and the previous check
    /// ("expiring soon") returned false for a blank token, so nothing refreshed until the seller
    /// hit an API and got an error.
    /// </remarks>
    public static bool ShouldRefreshNow(
        string? accessToken, DateTimeOffset? accessExpiresAt,
        string? refreshToken, DateTimeOffset? refreshExpiresAt,
        DateTimeOffset now, TimeSpan? lead = null)
    {
        if (!IsRefreshTokenUsable(refreshToken, refreshExpiresAt, now)) return false;
        if (string.IsNullOrWhiteSpace(accessToken)) return true;
        if (accessExpiresAt is null) return true;
        return now >= accessExpiresAt.Value - (lead ?? DefaultRefreshLead);
    }

    /// <summary>Seconds-from-now to an absolute instant, or null when eBay didn't say.</summary>
    public static DateTimeOffset? FromExpiresIn(int expiresInSeconds, DateTimeOffset now) =>
        expiresInSeconds > 0 ? now.AddSeconds(expiresInSeconds) : null;
}
