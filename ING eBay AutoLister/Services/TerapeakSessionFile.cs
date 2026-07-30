using System.Text.Json;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// What the saved Terapeak (eBay) session on disk actually is. Five answers, because
/// <c>File.Exists</c> was one answer covering five situations with four different fixes.
/// </summary>
public enum TerapeakSessionState
{
    /// <summary>No session has ever been saved here, or it was disconnected.</summary>
    Missing,

    /// <summary>The file is there and is zero bytes — a crash or a second login window caught mid-save.</summary>
    Empty,

    /// <summary>The file has content, but it is not a browser storageState this app can replay.</summary>
    Malformed,

    /// <summary>A real session that carries no eBay login any more, or one a research call was bounced out of.</summary>
    Expired,

    /// <summary>A storageState carrying eBay cookies, with nothing recorded against it.</summary>
    Valid,
}

/// <param name="State">Which of the five situations this is.</param>
/// <param name="Reason">One sentence for the seller. Never mentions a cookie's value, only its presence.</param>
public sealed record TerapeakSessionStatus(TerapeakSessionState State, string Reason)
{
    /// <summary>True only when a research call is worth launching a browser for.</summary>
    public bool CanSearch => State == TerapeakSessionState.Valid;

    /// <summary>
    /// True when the button to offer is Reconnect rather than Connect: there was a working login
    /// here and it died. Telling that seller they "need to connect Terapeak" reads as the app having
    /// forgotten something it was told, which is the complaint this whole file exists to answer.
    /// </summary>
    public bool NeedsReconnect => State == TerapeakSessionState.Expired;
}

/// <summary>
/// The saved Terapeak storageState, read as a state machine rather than as <c>File.Exists</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the Terapeak half of what <see cref="FacebookSessionFile"/> does for Facebook, and it
/// exists for the same measured failures — both services save a browser session written by
/// Playwright and both used to treat the file's mere presence as "connected":
/// </para>
/// <list type="bullet">
/// <item>A zero-byte or truncated file counted as connected, so every lookup launched a browser,
/// replayed nothing and came back with no comps. "No recent sales for this item" is a perfectly
/// ordinary answer, so a dead session was never reported as one — it just quietly made every price
/// estimate worse.</item>
/// <item>A session eBay had genuinely stopped accepting was <c>File.Delete</c>d on the spot, so the
/// next screen said "never connected" and the seller went looking for a setting they had already
/// set. It also threw away the one file ConnectionDoctor would otherwise replay to say what eBay
/// actually did.</item>
/// </list>
/// <para>
/// eBay has no single durable "you are signed in" cookie the way Facebook has <c>c_user</c> — which
/// is exactly why ConnectionDoctor's Terapeak check passes <c>cookieName: null</c>. So the most this
/// file can ask is whether the state carries eBay cookies at all; the verdict that counts is the
/// live page probe (see <see cref="TerapeakResearchPage"/>), and its answer arrives here as the
/// expired marker.
/// </para>
/// <para>
/// That marker is a sidecar file rather than an edit to the storageState: the session is
/// Playwright's document, not this app's, and a lookup that gets bounced to a sign-in page must not
/// risk rewriting the one copy of the seller's cookies to record that fact.
/// </para>
/// </remarks>
public static class TerapeakSessionFile
{
    /// <summary>Sidecar written when a research call is bounced. Holds a timestamp and a reason — never a cookie.</summary>
    public const string ExpiredMarkerSuffix = ".expired";

    public static string ExpiredMarkerPathFor(string sessionPath) => sessionPath + ExpiredMarkerSuffix;

    /// <summary>
    /// Reads <paramref name="sessionPath"/> and says which of the five states it is in. Never
    /// throws: an unreadable file is a state, not an error, and the caller's job is to tell the
    /// seller which button to press.
    /// </summary>
    public static TerapeakSessionStatus Inspect(string sessionPath)
    {
        if (string.IsNullOrWhiteSpace(sessionPath))
            return new(TerapeakSessionState.Missing, "No Terapeak session has been saved on this machine yet.");

        bool markerPresent;
        bool anyFile;
        try
        {
            markerPresent = File.Exists(ExpiredMarkerPathFor(sessionPath));
            anyFile = File.Exists(sessionPath) || File.Exists(AtomicFile.BackupPathFor(sessionPath));
        }
        catch
        {
            return new(TerapeakSessionState.Missing, "The saved Terapeak session couldn't be read from this machine.");
        }

        if (!anyFile)
            return new(TerapeakSessionState.Missing, "No Terapeak session has been saved on this machine yet.");

        // The backup is consulted before the file is declared broken — a truncated save with an
        // intact .bak beside it is a recoverable login, not a lost one. See AtomicFile.
        var text = AtomicFile.ReadWithRecovery(sessionPath, LooksLikeStorageState);
        if (text is null)
        {
            string raw;
            try { raw = File.Exists(sessionPath) ? File.ReadAllText(sessionPath) : ""; }
            catch { raw = ""; }

            return string.IsNullOrWhiteSpace(raw)
                ? new(TerapeakSessionState.Empty,
                    "The saved Terapeak session file is empty — a crash or a second login window caught it mid-save.")
                : new(TerapeakSessionState.Malformed,
                    "The saved Terapeak session file isn't a browser session this app can replay — it was most likely truncated by a crash mid-save.");
        }

        return Classify(text, markerPresent);
    }

    /// <summary>
    /// The judgement itself, on contents rather than a path — so every state is reachable in a test
    /// without a browser, a login or a temp file.
    /// </summary>
    public static TerapeakSessionStatus Classify(string? json, bool expiredMarkerPresent)
    {
        if (json is null || json.Trim().Length == 0)
            return new(TerapeakSessionState.Empty,
                "The saved Terapeak session file is empty — a crash or a second login window caught it mid-save.");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return new(TerapeakSessionState.Malformed,
                "The saved Terapeak session file isn't valid JSON, so it can't be replayed in a browser.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("cookies", out var cookies)
                || cookies.ValueKind != JsonValueKind.Array)
                return new(TerapeakSessionState.Malformed,
                    "The saved Terapeak session file has no cookies in it, so it isn't a browser session this app can replay.");

            // Same question ConnectionDoctor asks of this file, and for the same reason: eBay spreads
            // its sign-in across several cookies and renames them, so "carries eBay cookies at all"
            // is the strongest claim the file itself can support. Anything sharper would be guessing,
            // and a wrong guess here costs the seller a six-minute re-login for nothing.
            if (!ConnectionDoctor.HasLoginCookie(root, cookieName: null, domainFilter: "ebay.com"))
                return new(TerapeakSessionState.Expired,
                    "The saved Terapeak session no longer carries any eBay cookies, so the login it once held is gone.");

            if (expiredMarkerPresent)
                return new(TerapeakSessionState.Expired,
                    "The last Terapeak lookup was bounced to eBay's sign-in page, so the saved session is no longer accepted.");

            return new(TerapeakSessionState.Valid, "A saved Terapeak session is present and still carries eBay cookies.");
        }
    }

    /// <summary>
    /// Records that eBay refused this session, without touching the session itself.
    /// </summary>
    /// <remarks>
    /// Deliberately not a delete. Deleting makes the next screen say "never connected", which sends
    /// the seller looking for a setting they already set instead of pressing Reconnect — and throws
    /// away the file ConnectionDoctor would otherwise replay to say exactly what eBay did.
    /// </remarks>
    public static void MarkExpired(string sessionPath, string reason)
    {
        if (string.IsNullOrWhiteSpace(sessionPath)) return;
        try
        {
            AtomicFile.WriteAllText(ExpiredMarkerPathFor(sessionPath), $"{DateTimeOffset.UtcNow:u} {reason}");
        }
        catch
        {
            // A marker that could not be written costs one extra empty lookup, which is strictly
            // better than failing the lookup that just told us something useful.
        }
    }

    /// <summary>Clears the marker. Called when a fresh login verifies, so a new session starts clean.</summary>
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

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
