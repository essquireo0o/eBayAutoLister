using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The arithmetic behind "is this sign-in still good, and when do we go and renew it". It used to
// read UtcNow inside CredentialsStore, which made every one of these questions answerable only by
// waiting two hours for a real token to expire — so none of them were answered, and the two cases
// that mattered most were both wrong: an unknown expiry counted as "fresh forever" (so nothing
// ever refreshed it), and a missing access token counted as "nothing to refresh" (so the state a
// restart leaves behind was the one state the background top-up ignored).
public class EbayTokenExpiryTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    // ── Access token ─────────────────────────────────────────────────────────

    [Fact]
    public void Access_token_well_inside_its_life_is_not_expired()
    {
        Assert.False(EbayTokenExpiry.IsAccessTokenExpired(
            "token", Now.AddHours(1), hasRefreshToken: true, Now));
    }

    [Fact]
    public void Access_token_past_its_expiry_is_expired()
    {
        Assert.True(EbayTokenExpiry.IsAccessTokenExpired(
            "token", Now.AddSeconds(-1), hasRefreshToken: true, Now));
    }

    // The skew is the point: a token with eight seconds left is one that expires while the request
    // carrying it is still in flight, and eBay's answer to that is a 401 in the middle of a publish.
    [Fact]
    public void Access_token_inside_the_skew_window_is_already_expired()
    {
        var almost = Now + EbayTokenExpiry.AccessTokenSkew - TimeSpan.FromSeconds(5);

        Assert.True(EbayTokenExpiry.IsAccessTokenExpired("token", almost, hasRefreshToken: true, Now));
        Assert.False(EbayTokenExpiry.IsAccessTokenExpired(
            "token", Now + EbayTokenExpiry.AccessTokenSkew + TimeSpan.FromSeconds(5), true, Now));
    }

    [Fact]
    public void No_access_token_at_all_counts_as_expired()
    {
        Assert.True(EbayTokenExpiry.IsAccessTokenExpired("", null, hasRefreshToken: true, Now));
        Assert.True(EbayTokenExpiry.IsAccessTokenExpired(null, Now.AddHours(1), hasRefreshToken: false, Now));
    }

    // An unrecorded expiry is not evidence of freshness. If it can be renewed, renew it.
    [Fact]
    public void Unknown_expiry_is_expired_when_there_is_a_refresh_token_to_fix_it_with()
    {
        Assert.True(EbayTokenExpiry.IsAccessTokenExpired("token", null, hasRefreshToken: true, Now));
    }

    // ...but a hand-pasted token has nothing behind it, and refusing to use it helps nobody.
    [Fact]
    public void Unknown_expiry_is_usable_when_there_is_nothing_to_refresh_with()
    {
        Assert.False(EbayTokenExpiry.IsAccessTokenExpired("token", null, hasRefreshToken: false, Now));
    }

    // ── Refresh token ────────────────────────────────────────────────────────

    [Fact]
    public void Refresh_token_with_months_left_is_usable()
    {
        Assert.True(EbayTokenExpiry.IsRefreshTokenUsable("refresh", Now.AddDays(400), Now));
    }

    [Fact]
    public void Refresh_token_inside_its_last_day_is_not_worth_spending_a_call_on()
    {
        Assert.False(EbayTokenExpiry.IsRefreshTokenUsable("refresh", Now.AddHours(6), Now));
        Assert.False(EbayTokenExpiry.IsRefreshTokenUsable("refresh", Now.AddDays(-1), Now));
    }

    // eBay only reports refresh_token_expires_in on the first exchange, so a connection made before
    // that was recorded has no date — and is not thereby dead.
    [Fact]
    public void Refresh_token_with_no_recorded_expiry_is_trusted()
    {
        Assert.True(EbayTokenExpiry.IsRefreshTokenUsable("refresh", null, Now));
    }

    [Fact]
    public void No_refresh_token_is_never_usable()
    {
        Assert.False(EbayTokenExpiry.IsRefreshTokenUsable("", Now.AddDays(400), Now));
        Assert.False(EbayTokenExpiry.IsRefreshTokenUsable(null, null, Now));
    }

    // ── When the background loop should act ──────────────────────────────────

    private static bool ShouldRefresh(
        string? access, DateTimeOffset? accessExpiry, string? refresh = "refresh",
        DateTimeOffset? refreshExpiry = null, TimeSpan? lead = null) =>
        EbayTokenExpiry.ShouldRefreshNow(access, accessExpiry, refresh, refreshExpiry ?? Now.AddDays(400), Now, lead);

    [Fact]
    public void Refresh_fires_before_expiry_not_after_it()
    {
        var lead = TimeSpan.FromMinutes(20);

        // Nineteen minutes to go: inside the lead, so the top-up happens while the token still works.
        Assert.True(ShouldRefresh("token", Now.AddMinutes(19), lead: lead));
        // Twenty-one minutes: nothing to do yet, and the loop comes back every five.
        Assert.False(ShouldRefresh("token", Now.AddMinutes(21), lead: lead));
    }

    // The state every restart lands in: the access token is gone, the 18-month refresh token is
    // right there in credentials.json. The old check returned false for a blank token, so nothing
    // refreshed until the seller hit an API and got an error.
    [Fact]
    public void Refresh_fires_when_there_is_no_access_token_but_a_good_refresh_token()
    {
        Assert.True(ShouldRefresh("", null));
    }

    [Fact]
    public void Refresh_fires_when_the_access_token_expiry_was_never_recorded()
    {
        Assert.True(ShouldRefresh("token", null));
    }

    // Without a usable refresh token there is nothing to refresh with, and a call to eBay would be
    // a guaranteed failure — which is worse than nothing, because it produces a scary log line.
    [Fact]
    public void Refresh_does_not_fire_without_a_usable_refresh_token()
    {
        Assert.False(ShouldRefresh("token", Now.AddMinutes(1), refresh: ""));
        Assert.False(ShouldRefresh("token", Now.AddMinutes(1), refreshExpiry: Now.AddHours(2)));
    }

    [Fact]
    public void ExpiresIn_of_zero_records_no_expiry_rather_than_the_epoch()
    {
        Assert.Null(EbayTokenExpiry.FromExpiresIn(0, Now));
        Assert.Equal(Now.AddSeconds(7200), EbayTokenExpiry.FromExpiresIn(7200, Now));
    }
}
