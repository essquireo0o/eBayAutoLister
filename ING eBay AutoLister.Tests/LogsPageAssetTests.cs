namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The Logs screen is the one a seller opens when something has already gone wrong, usually so they
/// can send it to somebody. Three things about it are decisions, and each is easy to undo by
/// accident in a redesign because nothing in C# renders the page:
/// <para>
/// a warning or an error is carried by the whole row and by a verdict line above the list, not by
/// the tint of a small pill; the level filter is four visible buttons, not a dropdown or a fold;
/// and "Copy for support" never puts a log entry on the clipboard without passing it through the
/// redactor first — a log detail is whatever eBay or an exception said, credential included.
/// </para>
/// The redactor's behaviour on real token shapes is exercised in a browser
/// (verification/logs-page-check.mjs); this pins that the path to the clipboard goes through it.
/// </summary>
public class LogsPageAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");

    private static string LogsHtml => Section(Html,
        "<section id=\"logs-section\"",
        "<section id=\"form-section\"");

    [Theory]
    [InlineData("btn-copy-logs")]
    [InlineData("btn-refresh-logs")]
    [InlineData("logs-summary")]
    [InlineData("logs-filter")]
    [InlineData("logs-copy-fallback-wrap")]
    [InlineData("logs-copy-fallback")]
    [InlineData("logs-list")]
    public void Every_control_the_page_binds_is_still_on_the_page(string id)
    {
        Assert.True(LogsHtml.Contains($"id=\"{id}\"", StringComparison.Ordinal),
            $"#{id} is read by app.js but no longer exists inside #logs-section");
    }

    [Theory]
    [InlineData("all")]
    [InlineData("error")]
    [InlineData("warning")]
    [InlineData("info")]
    public void The_level_filter_is_four_buttons_in_plain_sight(string filter)
    {
        var logs = LogsHtml;

        Assert.Contains($"data-log-filter=\"{filter}\"", logs, StringComparison.Ordinal);
        // "Easy" here means bigger and clearer, never tucked away: no fold, no dropdown.
        Assert.DoesNotContain("<details", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("<select", logs, StringComparison.Ordinal);
    }

    [Fact]
    public void The_filter_and_the_copy_button_are_wired()
    {
        Assert.Contains("on('btn-copy-logs', 'click', copyLogsForSupport);", Js, StringComparison.Ordinal);
        Assert.Contains("if (pick) setLogFilter(pick.dataset.logFilter);", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_warning_and_error_count_as_something_going_wrong()
    {
        // The server also writes Research, Sourcing and Negotiation. Those are the app working.
        var kind = Section(Js, "function logKind(entry)", "function shownLogEntries()");

        Assert.Contains("return 'error';", kind, StringComparison.Ordinal);
        Assert.Contains("return 'warning';", kind, StringComparison.Ordinal);
        Assert.Contains("return 'info';", kind, StringComparison.Ordinal);
    }

    [Fact]
    public void A_warning_or_error_marks_the_whole_row_not_just_the_pill()
    {
        Assert.Contains("class=\"log-row log-row--${kind}\"", Js, StringComparison.Ordinal);

        foreach (var kind in new[] { "warning", "error" })
        {
            var rule = Section(Css, $".log-row--{kind} {{", "}");
            Assert.Contains("background:", rule, StringComparison.Ordinal);
            Assert.Contains("box-shadow: inset 5px 0 0", rule, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_verdict_line_says_so_when_nothing_has_gone_wrong()
    {
        var chrome = Section(Js, "function renderLogChrome()", "function renderLogs()");

        // Silence is not reassurance: a clean log says it is clean.
        Assert.Contains("Nothing has gone wrong.", chrome, StringComparison.Ordinal);
        Assert.Contains("logs-summary--ok", chrome, StringComparison.Ordinal);
        Assert.Contains("logs-summary--${worst}", chrome, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_errors_list_reads_as_good_news_with_a_way_back()
    {
        var render = Section(Js, "function renderLogs()", "function logRow(entry)");

        Assert.Contains("'No errors'", render, StringComparison.Ordinal);
        Assert.Contains("'Show everything'", render, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_reaches_the_clipboard_without_going_through_the_redactor()
    {
        var build = Section(Js, "function supportLogText(entries)", "async function copyLogsForSupport()");
        var copy = Section(Js, "async function copyLogsForSupport()", "async function loadListings(");

        // Both free-text fields of an entry are cleaned, and neither is used raw anywhere else.
        Assert.Contains("clean(entry.title || 'Action')", build, StringComparison.Ordinal);
        Assert.Contains("clean(entry.detail || '')", build, StringComparison.Ordinal);
        Assert.Equal(1, Count(build, "entry.title"));
        Assert.Equal(1, Count(build, "entry.detail"));
        Assert.Contains("redactSecrets(value)", build, StringComparison.Ordinal);

        // The only text the copy writes — clipboard or the Ctrl+C fallback box — is that text.
        Assert.Contains("const { text, hidden } = supportLogText(shown);", copy, StringComparison.Ordinal);
        Assert.Contains("navigator.clipboard.writeText(text)", copy, StringComparison.Ordinal);
        Assert.Contains("box.value = text;", copy, StringComparison.Ordinal);
        Assert.DoesNotContain("entry.detail", copy, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("(Bearer|Basic)")]                 // Authorization headers
    [InlineData(@"v\^1\.1#")]                      // eBay user / refresh tokens
    [InlineData(@"\bsk-")]                         // AI provider keys
    [InlineData(@"\beyJ")]                         // JWTs
    [InlineData("(?:PRD|SBX)")]                    // eBay App ID / Cert ID
    [InlineData(@"client[\s_-]?secret")]           // name=value secrets
    [InlineData("password")]
    [InlineData("(?:code|state|sig|key)=")]        // one-time codes in a web address
    [InlineData("{32,}")]                          // anything else long and unbroken
    public void The_redactor_knows_every_shape_a_credential_takes_here(string shape)
    {
        var rules = Section(Js, "const LOG_SECRET_RULES = [", "function redactSecrets(value)");

        Assert.Contains(shape, rules, StringComparison.Ordinal);
    }

    [Fact]
    public void A_browser_that_refuses_to_copy_still_leaves_the_text_one_keystroke_away()
    {
        var copy = Section(Js, "async function copyLogsForSupport()", "async function loadListings(");

        Assert.Contains("box.select();", copy, StringComparison.Ordinal);
        Assert.Contains("press Ctrl+C", LogsHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_says_before_the_click_that_secrets_are_hidden()
    {
        Assert.Contains("Passwords, tokens and keys are hidden first.", LogsHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stamps_moved_so_no_browser_keeps_the_old_log_screen()
    {
        AssetStamp.AtLeast(Html, "app.js?v=", 174);
        AssetStamp.AtLeast(Html, "style.css?v=", 142);
    }

    private static int Count(string source, string needle)
    {
        var count = 0;
        for (var at = source.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = source.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string Section(string source, string from, string to)
    {
        var start = source.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"could not find \"{from}\"");
        var end = source.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
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
