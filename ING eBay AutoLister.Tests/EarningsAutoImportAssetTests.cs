namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The Money Made screen imports sold orders from the REQUEST context, not from a background
/// service. This matters only on the hosted build and nothing in C# renders it, so nothing in C#
/// notices when the wiring rots — and when it does, the screen sits on "Reading your eBay sales…"
/// forever with no data, exactly as it did before this fix.
/// </summary>
/// <remarks>
/// Why the request context and not the background importer: on hosted every seller's eBay token is
/// per-user and resolved from their HttpContext (see UserScope / PerUserData). A singleton
/// BackgroundService has no request, so it sees "not connected" for everyone and imports for nobody.
/// The one place the token IS resolvable per-user is the request the open screen makes — so that is
/// where the import has to run. These tests lock that it does, and that the status stops lying about
/// it.
/// </remarks>
public class EarningsAutoImportAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");

    // Opening the screen is what triggers the import — there is no button, and on hosted the
    // background importer never runs for this user.
    [Fact]
    public void Opening_the_screen_triggers_a_request_context_import()
    {
        var fn = Function(Js, "function showEarningsSection() {");
        Assert.Contains("maybeAutoImportEarnings();", fn, StringComparison.Ordinal);
    }

    // The import goes through the per-user endpoint, which resolves the token from the request.
    [Fact]
    public void The_import_posts_to_the_per_user_endpoint()
    {
        var fn = Function(Js, "async function runEarningsRequestImport() {");
        Assert.Contains("'/api/earnings/import'", fn, StringComparison.Ordinal);
        Assert.Contains("method: 'POST'", fn, StringComparison.Ordinal);
    }

    // One import at a time on the client (the server serialises too, via the import gate), and never
    // a second one once the first has worked this page-session.
    [Fact]
    public void A_second_import_is_guarded_against()
    {
        var fn = Function(Js, "async function maybeAutoImportEarnings() {");
        Assert.Contains("if (earningsImportInFlight) return;", fn, StringComparison.Ordinal);
        Assert.Contains("if (earningsRequestImport?.ok) return;", fn, StringComparison.Ordinal);
    }

    // The bug itself: the status read only the singleton background service's lastSuccessUtc, which
    // is ALWAYS null on hosted, so it showed "Reading your eBay sales…" even after a successful
    // per-user import. The request-import result has to win over the server's fields.
    [Fact]
    public void The_status_reflects_the_per_user_import_not_the_dead_singleton_field()
    {
        var fn = Function(Js, "function renderEarningsAutoStatus(s) {");

        // The in-flight readout is driven by the client flag, not by the server saying nothing yet.
        Assert.Contains("if (earningsImportInFlight) {", fn, StringComparison.Ordinal);

        // And a finished request-import shows its own success/failure BEFORE the code ever falls
        // through to the singleton's (always-null-on-hosted) lastSuccessUtc branch.
        var inFlightAt   = fn.IndexOf("if (earningsImportInFlight) {", StringComparison.Ordinal);
        var requestAt    = fn.IndexOf("if (earningsRequestImport) {", StringComparison.Ordinal);
        var singletonAt  = fn.IndexOf("if (!s.lastSuccessUtc) {", StringComparison.Ordinal);
        Assert.True(inFlightAt >= 0 && requestAt > inFlightAt,
            "the request-import result must be considered after the in-flight check");
        Assert.True(singletonAt > requestAt,
            "the request-import result must win over the singleton background service's field");
    }

    // A cached app.js is an app that runs the old, background-only status against the new flow —
    // the very "Reading… forever" this change removes.
    [Fact]
    public void The_asset_version_was_bumped()
    {
        Assert.True(AssetVersion(Html, "app.js") >= 150, "app.js cache-buster went backwards");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static int AssetVersion(string html, string file)
    {
        var marker = file + "?v=";
        var at = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, file + " is no longer cache-busted at all");
        var digits = new string(html[(at + marker.Length)..].TakeWhile(char.IsDigit).ToArray());
        Assert.True(digits.Length > 0, file + "?v= carries no version number");
        return int.Parse(digits);
    }

    // From a function's opening line to the closing brace at its own indentation.
    private static string Function(string js, string signature)
    {
        var start = js.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"could not find `{signature}` in app.js");

        var lineStart = js.LastIndexOf('\n', start) + 1;
        var indent = js[lineStart..start];
        var end = js.IndexOf($"\n{indent}}}", start + signature.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"could not find the end of `{signature}`");
        return js[start..end];
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
