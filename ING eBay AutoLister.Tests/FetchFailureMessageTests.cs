using System.Text.RegularExpressions;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The seller clicked "Scan My Account" on the Listing Copilot and got a red bar reading
/// <c>Could not scan: Failed to fetch</c>.
///
/// "Failed to fetch" is the browser's own words for "this request never reached a server" — a
/// sentence about the fetch API, shown to someone who sells on eBay. It named nothing they could
/// act on, and it read as "the app is broken". What had actually happened was simple and fixable:
/// the backend had been closed while the page stayed open, so every call the page made died at the
/// network layer.
///
/// The panel had a whole reliability layer available to it (<c>callApi</c>, <c>renderFailure</c>)
/// and used none of it — the handler was <c>catch (e) { 'Could not scan: ' + e.message }</c>,
/// printing the exception straight through. These tests hold the two halves of the fix: the Copilot
/// calls go through the shared layer, and no caught exception anywhere in this file reaches the
/// screen as its raw message.
/// </summary>
public class FetchFailureMessageTests
{
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");

    // ── The Copilot scan, which is where the seller met this ─────────────────

    [Fact]
    public void TheCopilotScanGoesThroughTheSharedCaller()
    {
        var scan = Block("async function runCopilotScan(");

        Assert.Contains("callApi('/api/copilot/scan'", scan);

        // The two shapes this replaced. `fetch(...).then(r => r.json())` and a bare `await
        // fetch(...)` both end a dead backend as an unhandled rejection rather than a sentence.
        Assert.DoesNotContain("fetch('/api/copilot/scan')", scan);
        Assert.DoesNotContain(".then(r => r.json())", scan);
    }

    [Fact]
    public void TheCopilotScanNeverPrintsTheRawRejection()
    {
        var scan = Block("async function runCopilotScan(");

        // The exact line that produced the screenshot.
        Assert.DoesNotContain("'Could not scan: ' + e.message", scan);

        // And nothing else in it concatenates a caught exception into text either — no `e.message`,
        // no `err.message`, no `String(e)`.
        Assert.DoesNotMatch(new Regex(@"\b(?:err|e|ex)\??\.message\b"), scan);
        Assert.DoesNotMatch(new Regex(@"String\(\s*(?:err|e|ex)\s*\)"), scan);

        // It reports through the one renderer, which puts what happened, what to do and the button
        // that does it on screen — and keeps the raw text folded away as evidence.
        Assert.Contains("showCopilotFailure", scan);
    }

    [Fact]
    public void EveryCopilotCallUsesTheSharedCaller()
    {
        // The scan was the one the seller hit, but a panel with one honest error path and four
        // hand-rolled ones is a panel that will show a browser internal again next week.
        foreach (var endpoint in new[]
                 {
                     "/api/copilot/scan",
                     "/api/copilot/improve-seo/start",
                     "/api/copilot/improve-seo/status",
                     "/api/copilot/improve-seo/cancel",
                 })
        {
            Assert.Contains($"callApi('{endpoint}'", Js);
            Assert.DoesNotContain($"fetch('{endpoint}'", Js);
        }
    }

    [Fact]
    public void AFailedScanOffersTheWayForwardRatherThanTheRetryAlone()
    {
        var scan = Block("async function runCopilotScan(");

        // A Try again button, wired to actually re-run the scan.
        Assert.Contains("() => runCopilotScan()", scan);

        // And when it was the app that had gone, the scan the seller asked for runs itself once the
        // app is back — rather than making them find the button a second time.
        Assert.Contains("whenBackendReturns", scan);
    }

    // ── The words themselves ─────────────────────────────────────────────────

    [Fact]
    public void TheBrowsersOwnPhraseIsNeverWhatTheSellerReads()
    {
        // Not as a literal anywhere it could be shown, and not assembled either: `errorText` and
        // `unreachableFailure` are the only two ways a rejected fetch becomes words, and neither
        // reads `.message` on the network path.
        foreach (var browserPhrase in new[] { "Failed to fetch", "NetworkError when attempting", "Load failed" })
            Assert.DoesNotContain($"'{browserPhrase}", Js);

        var errorText = Block("function errorText(");
        Assert.Contains("isUnreachable(err)", errorText);
        Assert.Contains("UNREACHABLE_SENTENCE", errorText);

        // The sentence a seller gets instead names the app, the state it is in, and the one thing
        // that fixes it.
        var sentence = Between("const UNREACHABLE_SENTENCE =", ";");
        Assert.Contains("ING AutoLister is not running", sentence);
        Assert.Contains("Start the app again", sentence);
    }

    [Fact]
    public void EvenTheTechnicalDetailIsWrittenRatherThanPasted()
    {
        // The evidence block is still shown on screen, folded away. Pasting "Failed to fetch" into
        // it would put the useless phrase back one click deeper, so the unreachable case describes
        // itself: which request, and what kind of failure.
        var technical = Block("function technicalDetail(");
        Assert.Contains("isUnreachable(err)", technical);
        Assert.Contains("never reached", technical);
        Assert.Contains("err?.name", technical);
    }

    [Fact]
    public void NoCaughtExceptionReachesTheScreenAsItsRawMessage()
    {
        // The sweep. Everything below the reliability layer routes a caught exception through
        // `errorText` or `technicalDetail`; reading `.message` off it directly is the bug this file
        // exists to prevent, in all ~137 call sites rather than only the one that was reported.
        var body = Js[(Js.IndexOf("// ── Crash recovery:", StringComparison.Ordinal))..];

        var leaks = Regex.Matches(body, @"^.*\b(?:err|e|ex)\??\.message\b.*$", RegexOptions.Multiline)
            .Select(m => m.Value.Trim())
            .ToList();

        Assert.True(leaks.Count == 0,
            "these lines read a caught exception's message directly instead of going through "
            + "errorText/technicalDetail:\n  " + string.Join("\n  ", leaks));
    }

    [Fact]
    public void TheFourFailuresASellerWouldActOnDifferentlyAreToldApart()
    {
        // Restarting the app, waiting, retrying and reporting a bug are four different next steps.
        // One "something went wrong" for all four is what sends people to the wrong one.
        var callApi = Block("async function callApi(");

        Assert.Contains("kind: 'Timeout'", callApi);       // gave up waiting
        Assert.Contains("unreachableFailure", callApi);    // nothing there to answer
        Assert.Contains("'Unreadable'", callApi);          // answered with something unparseable
        Assert.Contains("'Unknown'", callApi);             // answered, but with an error

        // An endpoint that explained itself has that explanation read out, rather than buried under
        // a status code.
        Assert.Contains("serverSaid", callApi);
    }

    // ── Coming back ──────────────────────────────────────────────────────────

    [Fact]
    public void ThePageNoticesTheAppComeBackWithoutBeingReloaded()
    {
        // A seller who restarts the app should not also have to work out that the page is stale.
        Assert.Contains("const BACKEND_PROBE_URL = '/api/app/instance'", Js);
        Assert.Contains("function noteBackendUnreachable()", Js);
        Assert.Contains("function noteBackendReachable()", Js);
        Assert.Contains("setInterval(probeBackend", Js);

        // The probe target has to be the one endpoint that answers with no services behind it and
        // before setup is complete — anything heavier turns a knock into a real request.
        Assert.Equal("/api/app/instance", AppInstance.IdentityPath);

        // Both shared callers report a live app, so any successful request clears the banner.
        Assert.Contains("noteBackendReachable();", Block("async function callApi("));
        Assert.Contains("noteBackendReachable();", Block("async function localFetchJson("));

        // And the banner says the outcome out loud rather than just vanishing.
        Assert.Contains("Reconnected.", Js);
        Assert.Contains(".app-offline-banner", Css);
    }

    [Fact]
    public void TheOfflineBannerSaysWhatIsWrongAndWhatFixesIt()
    {
        var banner = Between("const OFFLINE_BANNER_TEXT =", ";");
        Assert.Contains("ING AutoLister is not running", banner);
        Assert.Contains("system tray", banner);
        Assert.Contains("reconnect on its own", banner);
        Assert.DoesNotContain("fetch", banner);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>The body of a top-level function in app.js, up to its closing brace at column 2.</summary>
    private static string Block(string signature)
    {
        var start = Js.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' is gone from app.js");
        var end = Js.IndexOf("\n  }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"could not find the end of '{signature}'");
        return Js[start..end];
    }

    private static string Between(string from, string to)
    {
        var start = Js.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{from}' is gone from app.js");
        var end = Js.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"could not find '{to}' after '{from}'");
        return Js[start..end];
    }

    private static string ReadAsset(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", "wwwroot", name));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
