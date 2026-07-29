using System.Text.Json;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// What the saved Facebook session on disk actually is. Five answers, because "the file exists"
/// was one answer covering five situations with three different fixes.
/// </summary>
public enum FacebookSessionState
{
    /// <summary>No session has ever been saved here, or it was disconnected.</summary>
    Missing,

    /// <summary>The file is there and is zero bytes — a crash or a second instance caught mid-save.</summary>
    Empty,

    /// <summary>The file has content, but it is not a browser storageState this app can replay.</summary>
    Malformed,

    /// <summary>A real session that no longer holds a login, or one a scrape was bounced out of.</summary>
    Expired,

    /// <summary>A storageState carrying Facebook's own signed-in cookie, unexpired.</summary>
    Valid,
}

/// <param name="State">Which of the five situations this is.</param>
/// <param name="Reason">One sentence for the seller. Never mentions the cookie's value, only its presence.</param>
public sealed record FacebookSessionStatus(FacebookSessionState State, string Reason)
{
    /// <summary>True only when a scrape is worth launching a browser for.</summary>
    public bool CanSearch => State == FacebookSessionState.Valid;

    /// <summary>
    /// True when the right thing to offer is Reconnect rather than Connect: there was a working
    /// login here and it died. Telling that seller they "need to connect Facebook" reads as the app
    /// having forgotten something, which is exactly the complaint this whole file exists to answer.
    /// </summary>
    public bool NeedsReconnect => State == FacebookSessionState.Expired;
}

/// <summary>
/// The saved Facebook storageState, read as a state machine rather than as
/// <c>File.Exists</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every state below was previously reported as one of two things, and both were wrong often
/// enough to cost a seller their login:
/// </para>
/// <list type="bullet">
/// <item>A zero-byte or truncated file — a crash mid-save, or two login windows writing at once —
/// counted as connected, so every search launched a browser, replayed nothing, and came back with
/// no listings. "Facebook Marketplace found nothing near me" is indistinguishable from a thin local
/// market, so it was never reported as a broken session at all. Worse, loading it into Playwright
/// throws, which surfaced as a crashed scrape rather than "you need to reconnect".</item>
/// <item>A session whose login had genuinely expired was deleted on the spot, which made the next
/// screen say "never connected" — so the seller was told to set up something they had already set
/// up, with no hint that anything had changed.</item>
/// </list>
/// <para>
/// Nothing here reads a cookie value, and nothing here logs one. The only question asked of the
/// file is whether Facebook's <c>c_user</c> marker is present and in date — the same marker the
/// login flow waits for (see FacebookMarketplaceService) and the same one ConnectionDoctor checks.
/// </para>
/// <para>
/// The expired marker is a sidecar file rather than an edit to the storageState: the session is
/// Playwright's document, not this app's, and a scrape that gets bounced to a login page must not
/// risk rewriting the one copy of the seller's cookies to record that fact.
/// </para>
/// </remarks>
public static class FacebookSessionFile
{
    /// <summary>Sidecar written when a scrape is bounced to login. Holds a timestamp and a reason — never a cookie.</summary>
    public const string ExpiredMarkerSuffix = ".expired";

    public static string ExpiredMarkerPathFor(string sessionPath) => sessionPath + ExpiredMarkerSuffix;

    /// <summary>
    /// Reads <paramref name="sessionPath"/> and says which of the five states it is in. Never
    /// throws: an unreadable file is a state, not an error, and the caller's job is to tell the
    /// seller which button to press.
    /// </summary>
    public static FacebookSessionStatus Inspect(string sessionPath, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(sessionPath))
            return new(FacebookSessionState.Missing, "No Facebook session has been saved on this machine yet.");

        bool markerPresent;
        bool anyFile;
        try
        {
            markerPresent = File.Exists(ExpiredMarkerPathFor(sessionPath));
            anyFile = File.Exists(sessionPath) || File.Exists(AtomicFile.BackupPathFor(sessionPath));
        }
        catch
        {
            return new(FacebookSessionState.Missing, "The saved Facebook session couldn't be read from this machine.");
        }

        if (!anyFile)
            return new(FacebookSessionState.Missing, "No Facebook session has been saved on this machine yet.");

        // The backup is consulted before the file is declared broken — a truncated save with an
        // intact .bak beside it is a recoverable login, not a lost one. See AtomicFile.
        var text = AtomicFile.ReadWithRecovery(sessionPath, LooksLikeStorageState);
        if (text is null)
        {
            string raw;
            try { raw = File.Exists(sessionPath) ? File.ReadAllText(sessionPath) : ""; }
            catch { raw = ""; }

            return string.IsNullOrWhiteSpace(raw)
                ? new(FacebookSessionState.Empty,
                    "The saved Facebook session file is empty — a crash or a second login window caught it mid-save.")
                : new(FacebookSessionState.Malformed,
                    "The saved Facebook session file isn't a browser session this app can replay — it was most likely truncated by a crash mid-save.");
        }

        return Classify(text, markerPresent, now ?? DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The judgement itself, on contents rather than a path — so every state is reachable in a test
    /// without a browser, a login or a temp file.
    /// </summary>
    public static FacebookSessionStatus Classify(string? json, bool expiredMarkerPresent, DateTimeOffset now)
    {
        if (json is null || json.Trim().Length == 0)
            return new(FacebookSessionState.Empty,
                "The saved Facebook session file is empty — a crash or a second login window caught it mid-save.");

        JsonElement root;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
            root = doc.RootElement;
        }
        catch (JsonException)
        {
            return new(FacebookSessionState.Malformed,
                "The saved Facebook session file isn't valid JSON, so it can't be replayed in a browser.");
        }

        using (doc)
        {
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("cookies", out var cookies)
                || cookies.ValueKind != JsonValueKind.Array)
                return new(FacebookSessionState.Malformed,
                    "The saved Facebook session file has no cookies in it, so it isn't a browser session this app can replay.");

            var (found, expiresAt) = FindSignInCookie(cookies);

            if (!found)
                return new(FacebookSessionState.Expired,
                    "The saved Facebook session no longer carries Facebook's own sign-in cookie, so the login it once held is gone.");

            if (expiresAt is { } expiry && expiry <= now)
                return new(FacebookSessionState.Expired,
                    "Facebook's sign-in cookie in the saved session has passed its expiry date.");

            if (expiredMarkerPresent)
                return new(FacebookSessionState.Expired,
                    "The last Marketplace search was bounced to Facebook's login page, so the saved session is no longer accepted.");

            return new(FacebookSessionState.Valid, "A saved Facebook session is present and still carries a sign-in.");
        }
    }

    /// <summary>
    /// Records that Facebook refused this session, without touching the session itself.
    /// </summary>
    /// <remarks>
    /// Deliberately not a delete. Deleting makes the next screen say "never connected", which sends
    /// the seller looking for a setting they already set instead of pressing Reconnect — and throws
    /// away the file ConnectionDoctor would otherwise replay to say exactly what Facebook did.
    /// </remarks>
    public static void MarkExpired(string sessionPath, string reason)
    {
        if (string.IsNullOrWhiteSpace(sessionPath)) return;
        try
        {
            AtomicFile.WriteAllText(ExpiredMarkerPathFor(sessionPath),
                $"{DateTimeOffset.UtcNow:u} {reason}");
        }
        catch
        {
            // A marker that could not be written costs one extra "no results" search, which is
            // strictly better than failing the search that just told us something useful.
        }
    }

    /// <summary>Clears the marker. Called when a fresh login lands, so a new session starts clean.</summary>
    public static void ClearExpiredMarker(string sessionPath)
    {
        if (string.IsNullOrWhiteSpace(sessionPath)) return;
        var marker = ExpiredMarkerPathFor(sessionPath);
        foreach (var path in new[] { marker, AtomicFile.BackupPathFor(marker), AtomicFile.TempPathFor(marker) })
            TryDelete(path);
    }

    /// <summary>
    /// Removes the session and everything written beside it. This is Disconnect — the seller asking
    /// for the login to be gone — so the backup goes too, or the next inspection recovers from it
    /// and reports the account they just disconnected as connected.
    /// </summary>
    public static void Delete(string sessionPath)
    {
        if (string.IsNullOrWhiteSpace(sessionPath)) return;
        foreach (var path in new[]
                 {
                     sessionPath,
                     AtomicFile.BackupPathFor(sessionPath),
                     AtomicFile.TempPathFor(sessionPath),
                 })
            TryDelete(path);
        ClearExpiredMarker(sessionPath);
    }

    /// <summary>
    /// True when <paramref name="text"/> parses as a storageState with a cookies array. Used as
    /// AtomicFile's validity test, so a complete-but-garbage file falls through to the backup.
    /// </summary>
    public static bool LooksLikeStorageState(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("cookies", out var cookies)
                && cookies.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Finds Facebook's <c>c_user</c> cookie and reads its expiry. Returns the expiry only when the
    /// file states a real one — Playwright writes <c>-1</c> for a session cookie, which means "lives
    /// as long as the browser does", not "expired in 1969".
    /// </summary>
    private static (bool Found, DateTimeOffset? ExpiresAt) FindSignInCookie(JsonElement cookies)
    {
        foreach (var cookie in cookies.EnumerateArray())
        {
            if (cookie.ValueKind != JsonValueKind.Object) continue;

            var domain = cookie.TryGetProperty("domain", out var d) ? d.GetString() ?? "" : "";
            if (!domain.Contains("facebook.com", StringComparison.OrdinalIgnoreCase)) continue;

            var name = cookie.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (name != "c_user") continue;

            // Present but empty is the same as absent: Facebook writes a blank c_user on logout.
            var value = cookie.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
            if (value.Length == 0) continue;

            DateTimeOffset? expires = null;
            if (cookie.TryGetProperty("expires", out var e)
                && e.ValueKind == JsonValueKind.Number
                && e.TryGetDouble(out var seconds)
                && seconds > 0)
            {
                try { expires = DateTimeOffset.FromUnixTimeSeconds((long)seconds); }
                catch (ArgumentOutOfRangeException) { expires = null; }
            }

            return (true, expires);
        }

        return (false, null);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
