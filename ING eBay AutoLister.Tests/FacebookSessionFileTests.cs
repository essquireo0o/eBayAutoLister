using System.Text.Json;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The saved Facebook session, read as five states rather than as <c>File.Exists</c>.
///
/// Every case below is a way a completed login was thrown away or misreported, and all of them
/// reached the seller as one of two useless sentences — "connect Facebook first" for a login they
/// had already done, or nothing at all, because a search that replayed a broken session found no
/// tiles and reported an empty local market.
///
/// Nothing here touches a browser, a cookie value or a network. The whole point of splitting the
/// judgement out of FacebookMarketplaceService is that all five answers are reachable from a string.
/// </summary>
public class FacebookSessionFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ing-fbsession-" + Guid.NewGuid().ToString("N"));

    public FacebookSessionFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string SessionPath => Path.Combine(_dir, "facebook-session.json");

    /// <summary>A storageState shaped like Playwright's, with one c_user cookie.</summary>
    private static string StorageState(string userValue = "100001234567890", double? expires = null) =>
        JsonSerializer.Serialize(new
        {
            cookies = new[]
            {
                new { name = "datr",   value = "abc",      domain = ".facebook.com", path = "/", expires = -1.0 },
                new { name = "c_user", value = userValue,  domain = ".facebook.com", path = "/",
                      expires = expires ?? DateTimeOffset.UtcNow.AddDays(90).ToUnixTimeSeconds() },
            },
            origins = Array.Empty<object>(),
        });

    // ── The five states, from a real file ────────────────────────────────────

    [Fact]
    public void No_file_at_all_is_Missing()
    {
        var status = FacebookSessionFile.Inspect(SessionPath);

        Assert.Equal(FacebookSessionState.Missing, status.State);
        Assert.False(status.CanSearch);
        // Missing is the one state where "Connect" is the right word — nothing was ever set up.
        Assert.False(status.NeedsReconnect);
    }

    // The exact file a crash mid-save leaves behind, and the reason AtomicFile exists. It used to
    // count as connected: every search launched a browser, replayed nothing, and came back with no
    // listings — which reads as "there's nothing for sale near me", not as a broken login.
    [Fact]
    public void A_zero_byte_file_is_Empty_and_not_a_crash()
    {
        File.WriteAllText(SessionPath, "");

        var status = FacebookSessionFile.Inspect(SessionPath);

        Assert.Equal(FacebookSessionState.Empty, status.State);
        Assert.False(status.CanSearch);
        Assert.NotEqual("", status.Reason);
    }

    [Fact]
    public void A_truncated_file_is_Malformed_and_not_a_crash()
    {
        File.WriteAllText(SessionPath, """{"cookies":[{"name":"c_us""");

        var status = FacebookSessionFile.Inspect(SessionPath);

        Assert.Equal(FacebookSessionState.Malformed, status.State);
        Assert.False(status.CanSearch);
    }

    // JSON that parses but is not a browser session — a stray file, or a half-written object that
    // happened to close. Loading this into Playwright throws; saying so here is the difference
    // between a named state and a crashed scrape.
    [Fact]
    public void Valid_JSON_that_is_not_a_storage_state_is_Malformed()
    {
        File.WriteAllText(SessionPath, """{"hello":"world"}""");

        Assert.Equal(FacebookSessionState.Malformed, FacebookSessionFile.Inspect(SessionPath).State);
    }

    [Fact]
    public void A_storage_state_with_a_live_c_user_cookie_is_Valid()
    {
        File.WriteAllText(SessionPath, StorageState());

        var status = FacebookSessionFile.Inspect(SessionPath);

        Assert.Equal(FacebookSessionState.Valid, status.State);
        Assert.True(status.CanSearch);
    }

    // The marker a bounced scrape leaves. Kept as a sidecar rather than a delete, because deleting
    // made the next screen say "never connected" — so the seller went looking for a setting they
    // had already set, with nothing anywhere telling them to sign in again.
    [Fact]
    public void A_good_file_with_an_expired_marker_beside_it_is_Expired()
    {
        File.WriteAllText(SessionPath, StorageState());
        FacebookSessionFile.MarkExpired(SessionPath, "bounced to Login");

        var status = FacebookSessionFile.Inspect(SessionPath);

        Assert.Equal(FacebookSessionState.Expired, status.State);
        Assert.False(status.CanSearch);
        Assert.True(status.NeedsReconnect);
        // The session itself is untouched: it is the seller's cookies, not this app's, and
        // ConnectionDoctor still needs it to ask Facebook what actually happened.
        Assert.True(File.Exists(SessionPath));
    }

    [Fact]
    public void A_fresh_login_clears_the_expired_marker()
    {
        File.WriteAllText(SessionPath, StorageState());
        FacebookSessionFile.MarkExpired(SessionPath, "bounced to Login");
        FacebookSessionFile.ClearExpiredMarker(SessionPath);

        Assert.Equal(FacebookSessionState.Valid, FacebookSessionFile.Inspect(SessionPath).State);
    }

    // ── The same five states, decided from contents alone ────────────────────

    [Fact]
    public void Empty_contents_are_Empty()
    {
        Assert.Equal(FacebookSessionState.Empty,
            FacebookSessionFile.Classify("   ", expiredMarkerPresent: false, DateTimeOffset.UtcNow).State);
        Assert.Equal(FacebookSessionState.Empty,
            FacebookSessionFile.Classify(null, expiredMarkerPresent: false, DateTimeOffset.UtcNow).State);
    }

    [Fact]
    public void Unparsable_contents_are_Malformed()
    {
        Assert.Equal(FacebookSessionState.Malformed,
            FacebookSessionFile.Classify("not json at all", false, DateTimeOffset.UtcNow).State);
    }

    // Facebook writes a blank c_user on logout rather than dropping the cookie, so "the cookie is
    // there" is not the question — "does it carry an account" is.
    [Fact]
    public void A_blank_or_absent_c_user_is_Expired_not_Valid()
    {
        Assert.Equal(FacebookSessionState.Expired,
            FacebookSessionFile.Classify(StorageState(userValue: ""), false, DateTimeOffset.UtcNow).State);

        var noSignIn = JsonSerializer.Serialize(new
        {
            cookies = new[] { new { name = "datr", value = "abc", domain = ".facebook.com", path = "/", expires = -1.0 } },
        });
        Assert.Equal(FacebookSessionState.Expired,
            FacebookSessionFile.Classify(noSignIn, false, DateTimeOffset.UtcNow).State);
    }

    [Fact]
    public void A_c_user_cookie_past_its_expiry_is_Expired()
    {
        var stale = StorageState(expires: DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds());

        Assert.Equal(FacebookSessionState.Expired,
            FacebookSessionFile.Classify(stale, false, DateTimeOffset.UtcNow).State);
    }

    // Playwright writes -1 for a cookie that lives as long as the browser does. Reading that as a
    // 1969 timestamp would report every session-cookie login as expired the moment it was saved.
    [Fact]
    public void A_session_cookie_with_no_expiry_is_not_treated_as_expired()
    {
        Assert.Equal(FacebookSessionState.Valid,
            FacebookSessionFile.Classify(StorageState(expires: -1), false, DateTimeOffset.UtcNow).State);
    }

    // ── Recovery and disconnect ──────────────────────────────────────────────

    // The whole reason the session is written to a temp and renamed: the .bak is a login that still
    // works, and falling back to it costs the seller nothing where reporting "not connected" costs
    // them a browser sign-in.
    [Fact]
    public void A_truncated_file_recovers_from_its_backup()
    {
        AtomicFile.WriteAllText(SessionPath, StorageState());
        AtomicFile.WriteAllText(SessionPath, StorageState());   // leaves the first as .bak
        File.WriteAllText(SessionPath, """{"cookies":[{"na""");  // crash mid-write

        Assert.Equal(FacebookSessionState.Valid, FacebookSessionFile.Inspect(SessionPath).State);
    }

    // Disconnect has to take the backup too. Leaving it means the next inspection recovers from it
    // and reports the account the seller just disconnected as connected.
    [Fact]
    public void Disconnecting_removes_the_backup_and_the_marker_as_well()
    {
        AtomicFile.WriteAllText(SessionPath, StorageState());
        AtomicFile.WriteAllText(SessionPath, StorageState());
        FacebookSessionFile.MarkExpired(SessionPath, "bounced to Login");

        FacebookSessionFile.Delete(SessionPath);

        Assert.Equal(FacebookSessionState.Missing, FacebookSessionFile.Inspect(SessionPath).State);
        Assert.False(File.Exists(AtomicFile.BackupPathFor(SessionPath)));
        Assert.False(File.Exists(FacebookSessionFile.ExpiredMarkerPathFor(SessionPath)));
    }

    // The marker holds a timestamp and a reason. It must never hold anything out of the session.
    [Fact]
    public void The_expired_marker_carries_no_session_data()
    {
        File.WriteAllText(SessionPath, StorageState(userValue: "100001234567890"));
        FacebookSessionFile.MarkExpired(SessionPath, "bounced to Checkpoint");

        var marker = File.ReadAllText(FacebookSessionFile.ExpiredMarkerPathFor(SessionPath));

        Assert.Contains("Checkpoint", marker);
        Assert.DoesNotContain("100001234567890", marker);
        Assert.DoesNotContain("c_user", marker);
    }

    // ── Where the file lives ─────────────────────────────────────────────────

    // The bug this replaces: bin\Debug, a copied build and the installed app each looked in their
    // own folder, so running a different build reported a connected account as disconnected.
    [Fact]
    public void The_session_path_is_named_through_AppPaths_not_the_working_directory()
    {
        Assert.Equal(Path.Combine(AppPaths.DataHome, AppPaths.FacebookSessionFileName),
            AppPaths.FacebookSessionPath);
        Assert.Contains(AppPaths.FacebookSessionFileName, AppPaths.StateFiles);
        Assert.NotEqual(Directory.GetCurrentDirectory(),
            Path.GetDirectoryName(AppPaths.FacebookSessionPath));
    }
}
