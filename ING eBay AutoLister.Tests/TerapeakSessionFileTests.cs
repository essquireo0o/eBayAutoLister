using System.Text.Json;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The saved Terapeak session, read as five states rather than as <c>File.Exists</c>.
///
/// Every case below is a way a completed eBay sign-in was thrown away or misreported, and all of
/// them reached the seller the same way: a sold-comps lookup that quietly found nothing. "No recent
/// sales for this item" is an ordinary answer, so a dead session never looked like one — it just
/// made every price estimate worse, with nothing anywhere saying "reconnect".
///
/// Nothing here touches a browser, a cookie value or the network. That is the point of splitting the
/// judgement out of TerapeakService: all five answers are reachable from a string.
/// </summary>
public class TerapeakSessionFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ing-tpsession-" + Guid.NewGuid().ToString("N"));

    public TerapeakSessionFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string SessionPath => Path.Combine(_dir, AppPaths.TerapeakSessionFileName);

    /// <summary>A storageState shaped like Playwright's, carrying eBay cookies.</summary>
    private static string StorageState(string domain = ".ebay.com") =>
        JsonSerializer.Serialize(new
        {
            cookies = new[]
            {
                new { name = "dp1",  value = "bpbf/#0000", domain, path = "/", expires = -1.0 },
                new { name = "nonsession", value = "BAQAAA", domain, path = "/",
                      expires = (double)DateTimeOffset.UtcNow.AddDays(90).ToUnixTimeSeconds() },
            },
            origins = Array.Empty<object>(),
        });

    // ── The five states, from a real file ────────────────────────────────────

    [Fact]
    public void No_file_at_all_is_Missing()
    {
        var status = TerapeakSessionFile.Inspect(SessionPath);

        Assert.Equal(TerapeakSessionState.Missing, status.State);
        Assert.False(status.CanSearch);
        // Missing is the one state where "Connect" is the right word — nothing was ever set up.
        Assert.False(status.NeedsReconnect);
    }

    // The exact file a crash mid-save leaves behind, and the reason the login writes through
    // AtomicFile. It used to count as connected: every lookup launched a browser, replayed nothing,
    // and came back with no comps — which the pricing code read as "this item never sells".
    [Fact]
    public void A_zero_byte_file_is_Empty_and_not_a_crash()
    {
        File.WriteAllText(SessionPath, "");

        var status = TerapeakSessionFile.Inspect(SessionPath);

        Assert.Equal(TerapeakSessionState.Empty, status.State);
        Assert.False(status.CanSearch);
        Assert.NotEqual("", status.Reason);
    }

    [Fact]
    public void A_truncated_file_is_Malformed_and_not_a_crash()
    {
        File.WriteAllText(SessionPath, """{"cookies":[{"name":"nonsess""");

        var status = TerapeakSessionFile.Inspect(SessionPath);

        Assert.Equal(TerapeakSessionState.Malformed, status.State);
        Assert.False(status.CanSearch);
    }

    // JSON that parses but is not a browser session — a stray file, or a half-written object that
    // happened to close. Naming the state is the difference between one sentence to the seller and
    // a scrape that throws.
    [Fact]
    public void Valid_JSON_that_is_not_a_storage_state_is_Malformed()
    {
        File.WriteAllText(SessionPath, """{"hello":"world"}""");

        Assert.Equal(TerapeakSessionState.Malformed, TerapeakSessionFile.Inspect(SessionPath).State);
    }

    [Fact]
    public void A_storage_state_carrying_eBay_cookies_is_Valid()
    {
        File.WriteAllText(SessionPath, StorageState());

        var status = TerapeakSessionFile.Inspect(SessionPath);

        Assert.Equal(TerapeakSessionState.Valid, status.State);
        Assert.True(status.CanSearch);
        Assert.False(status.NeedsReconnect);
    }

    // The marker a bounced research call leaves. A sidecar rather than a delete, because deleting
    // made the next screen say "never connected" — so the seller went looking for a setting they had
    // already set, instead of pressing the one button that fixes it.
    [Fact]
    public void A_good_file_with_an_expired_marker_beside_it_is_Expired()
    {
        File.WriteAllText(SessionPath, StorageState());
        TerapeakSessionFile.MarkExpired(SessionPath, "research call bounced to SignIn");

        var status = TerapeakSessionFile.Inspect(SessionPath);

        Assert.Equal(TerapeakSessionState.Expired, status.State);
        Assert.False(status.CanSearch);
        Assert.True(status.NeedsReconnect);
        // The session itself is untouched: it is the seller's cookies, not this app's, and
        // ConnectionDoctor still needs it to ask eBay what actually happened.
        Assert.True(File.Exists(SessionPath));
    }

    [Fact]
    public void A_verified_fresh_login_clears_the_expired_marker()
    {
        File.WriteAllText(SessionPath, StorageState());
        TerapeakSessionFile.MarkExpired(SessionPath, "research call bounced to SignIn");
        TerapeakSessionFile.ClearExpiredMarker(SessionPath);

        Assert.Equal(TerapeakSessionState.Valid, TerapeakSessionFile.Inspect(SessionPath).State);
    }

    // Without this, the very next lookup after a successful reconnect reports the session that just
    // succeeded as expired, because the marker from the login that died is still sitting there.
    [Fact]
    public void A_stale_marker_never_outlives_the_session_it_was_written_against()
    {
        File.WriteAllText(SessionPath, StorageState());
        TerapeakSessionFile.MarkExpired(SessionPath, "research call bounced to SignIn");
        Assert.True(TerapeakSessionFile.Inspect(SessionPath).NeedsReconnect);

        // What a confirmed reconnect does: rewrite the state, then clear the marker.
        AtomicFile.WriteAllText(SessionPath, StorageState());
        TerapeakSessionFile.ClearExpiredMarker(SessionPath);

        var status = TerapeakSessionFile.Inspect(SessionPath);
        Assert.Equal(TerapeakSessionState.Valid, status.State);
        Assert.True(status.CanSearch);
    }

    // ── The same states, decided from contents alone ─────────────────────────

    [Fact]
    public void Empty_contents_are_Empty()
    {
        Assert.Equal(TerapeakSessionState.Empty,
            TerapeakSessionFile.Classify("   ", expiredMarkerPresent: false).State);
        Assert.Equal(TerapeakSessionState.Empty,
            TerapeakSessionFile.Classify(null, expiredMarkerPresent: false).State);
    }

    [Fact]
    public void Unparsable_contents_are_Malformed()
    {
        Assert.Equal(TerapeakSessionState.Malformed,
            TerapeakSessionFile.Classify("not json at all", expiredMarkerPresent: false).State);
    }

    [Fact]
    public void A_storage_state_with_no_cookies_array_is_Malformed()
    {
        Assert.Equal(TerapeakSessionState.Malformed,
            TerapeakSessionFile.Classify("""{"origins":[]}""", expiredMarkerPresent: false).State);
    }

    // A session Playwright wrote for a browser that had been signed out: the file is a perfectly
    // well-formed storageState, and it carries nothing from eBay. Reporting it as Valid is how a
    // lookup spends forty seconds in a browser to be handed a sign-in page.
    [Fact]
    public void A_storage_state_carrying_no_eBay_cookies_is_Expired_not_Valid()
    {
        var elsewhere = StorageState(domain: ".facebook.com");

        var status = TerapeakSessionFile.Classify(elsewhere, expiredMarkerPresent: false);

        Assert.Equal(TerapeakSessionState.Expired, status.State);
        Assert.True(status.NeedsReconnect);
    }

    [Fact]
    public void An_empty_cookies_array_is_Expired_not_Valid()
    {
        Assert.Equal(TerapeakSessionState.Expired,
            TerapeakSessionFile.Classify("""{"cookies":[],"origins":[]}""", expiredMarkerPresent: false).State);
    }

    // eBay spreads its sign-in over several cookies and renames them, so "carries eBay cookies at
    // all" is the strongest claim the FILE can support — the verdict that counts is the live page
    // probe. Demanding a particular cookie name here would cost the seller a six-minute re-login
    // every time eBay renamed one.
    [Fact]
    public void No_single_cookie_name_is_required_for_Valid()
    {
        var unfamiliar = JsonSerializer.Serialize(new
        {
            cookies = new[] { new { name = "something_new_ebay_ships", value = "x", domain = "www.ebay.com", path = "/" } },
        });

        Assert.Equal(TerapeakSessionState.Valid,
            TerapeakSessionFile.Classify(unfamiliar, expiredMarkerPresent: false).State);
    }

    // Only one state may launch a browser, and only one state gets the Reconnect button. Asserted
    // together because the pairing is the whole contract the callers rely on.
    [Theory]
    [InlineData(TerapeakSessionState.Missing,   false, false)]
    [InlineData(TerapeakSessionState.Empty,     false, false)]
    [InlineData(TerapeakSessionState.Malformed, false, false)]
    [InlineData(TerapeakSessionState.Expired,   false, true)]
    [InlineData(TerapeakSessionState.Valid,     true,  false)]
    public void Only_Valid_can_search_and_only_Expired_needs_a_reconnect(
        TerapeakSessionState state, bool canSearch, bool needsReconnect)
    {
        var status = new TerapeakSessionStatus(state, "because");

        Assert.Equal(canSearch, status.CanSearch);
        Assert.Equal(needsReconnect, status.NeedsReconnect);
    }

    [Fact]
    public void Every_state_carries_a_sentence_for_the_seller()
    {
        string[] cases = ["", "   ", "not json", """{"hello":"world"}""", """{"cookies":[]}""", StorageState()];

        foreach (var text in cases)
            Assert.NotEqual("", TerapeakSessionFile.Classify(text, expiredMarkerPresent: false).Reason);
    }

    // A path this app never configured is not a crash — the caller's job is still to say which
    // button to press.
    [Fact]
    public void A_blank_path_is_Missing_rather_than_a_throw()
    {
        Assert.Equal(TerapeakSessionState.Missing, TerapeakSessionFile.Inspect("").State);
        Assert.Equal(TerapeakSessionState.Missing, TerapeakSessionFile.Inspect("   ").State);
    }

    // ── Recovery and disconnect ──────────────────────────────────────────────

    // The whole reason the session is written to a temp and renamed: the .bak is a login that still
    // works, and falling back to it costs the seller nothing where reporting "not connected" costs
    // them a browser sign-in they already sat through once.
    [Fact]
    public void A_truncated_file_recovers_from_its_backup()
    {
        AtomicFile.WriteAllText(SessionPath, StorageState());
        AtomicFile.WriteAllText(SessionPath, StorageState());   // leaves the first as .bak
        File.WriteAllText(SessionPath, """{"cookies":[{"na""");  // crash mid-write

        Assert.Equal(TerapeakSessionState.Valid, TerapeakSessionFile.Inspect(SessionPath).State);
    }

    // A complete file that is not a storageState must fall through to the backup too — otherwise the
    // validity test is just "is it JSON", and the .bak never gets consulted for the case it exists
    // for.
    [Fact]
    public void The_recovery_test_is_is_it_a_session_not_is_it_JSON()
    {
        Assert.True(TerapeakSessionFile.LooksLikeStorageState(StorageState()));
        Assert.False(TerapeakSessionFile.LooksLikeStorageState("""{"hello":"world"}"""));
        Assert.False(TerapeakSessionFile.LooksLikeStorageState("truncated{"));
    }

    // Disconnect has to take the backup too. Leaving it means the next inspection recovers from it
    // and reports the account the seller just disconnected as connected.
    [Fact]
    public void Disconnecting_removes_the_backup_and_the_marker_as_well()
    {
        AtomicFile.WriteAllText(SessionPath, StorageState());
        AtomicFile.WriteAllText(SessionPath, StorageState());
        TerapeakSessionFile.MarkExpired(SessionPath, "research call bounced to SignIn");

        TerapeakSessionFile.Delete(SessionPath);

        Assert.Equal(TerapeakSessionState.Missing, TerapeakSessionFile.Inspect(SessionPath).State);
        Assert.False(File.Exists(AtomicFile.BackupPathFor(SessionPath)));
        Assert.False(File.Exists(TerapeakSessionFile.ExpiredMarkerPathFor(SessionPath)));
    }

    // The marker holds a timestamp and a reason. It must never hold anything out of the session —
    // this file is the one place a cookie could leak into plain text beside a log.
    [Fact]
    public void The_expired_marker_carries_no_session_data()
    {
        File.WriteAllText(SessionPath, StorageState());
        TerapeakSessionFile.MarkExpired(SessionPath, "research call bounced to Subscription");

        var marker = File.ReadAllText(TerapeakSessionFile.ExpiredMarkerPathFor(SessionPath));

        Assert.Contains("Subscription", marker);
        Assert.DoesNotContain("BAQAAA", marker);
        Assert.DoesNotContain("nonsession", marker);
    }

    // ── Where the session and the profile live ───────────────────────────────

    // The bug this replaces: bin\Debug, a copied build and the installed app each looked in their
    // own folder, so running a different build reported a connected account as disconnected.
    [Fact]
    public void The_session_path_is_named_through_AppPaths_not_the_working_directory()
    {
        Assert.Equal(Path.Combine(AppPaths.DataHome, AppPaths.TerapeakSessionFileName),
            AppPaths.TerapeakSessionPath);
        Assert.Contains(AppPaths.TerapeakSessionFileName, AppPaths.StateFiles);
        Assert.NotEqual(Directory.GetCurrentDirectory(),
            Path.GetDirectoryName(AppPaths.TerapeakSessionPath));
    }

    // This one matters more than the session file does: the cookies actually travel in the profile
    // directory, so a build resolving it elsewhere hands eBay a browser it has never seen — which is
    // what a stolen session looks like, and gets the login challenged or killed.
    [Fact]
    public void The_browser_profile_is_named_through_AppPaths_and_carried_with_the_state()
    {
        Assert.Equal(Path.Combine(AppPaths.DataHome, AppPaths.TerapeakProfileDirName),
            AppPaths.TerapeakProfilePath);
        Assert.Contains(AppPaths.TerapeakProfileDirName, AppPaths.StateFolders);
    }
}
