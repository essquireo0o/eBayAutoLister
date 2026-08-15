using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Renders a submission, or what became of one, as plain text.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <see cref="AmazonListingFillReport"/>, with one section it does not have: the
/// exchange itself, quoted. That section is the report's whole reason for existing. "Amazon rejected
/// it" is not something anyone can act on — the issue codes are bare numbers, the messages quote
/// attribute paths, and the first question is always what was actually sent.
/// </para>
/// <para>
/// It is also this phase's acceptance artefact. A phase whose evidence is "a submission was made"
/// has no evidence; a phase that prints the request and the response has some, and can be checked by
/// somebody who does not trust it.
/// </para>
/// </remarks>
public static class AmazonSubmissionReport
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const int Rule = 78;

    public static string Describe(AmazonSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);

        var lines = new List<string>
        {
            $"SKU          : {Or(submission.Sku, "—")}",
            $"Environment  : {Or(submission.Environment, "—")}   Marketplace: {Or(submission.MarketplaceId, "—")}",
            $"Product type : {Or(submission.ProductType, "—")}",
            $"State        : {submission.State}" +
                (string.IsNullOrWhiteSpace(submission.AmazonStatus)
                    ? "   (Amazon gave no status)"
                    : $"   Amazon says: {submission.AmazonStatus}"),
        };

        if (!string.IsNullOrWhiteSpace(submission.SubmissionId))
            lines.Add($"Submission   : {submission.SubmissionId}");

        lines.Add("");
        lines.AddRange(Wrap(submission.Headline, Rule));

        if (!string.IsNullOrWhiteSpace(submission.NextAction))
        {
            lines.Add("");
            lines.Add("NEXT");
            lines.Add(new string('-', Rule));
            lines.AddRange(Wrap(submission.NextAction, Rule));
        }

        lines.AddRange(IssueSection(submission.Issues));
        lines.AddRange(CallSection(submission.Call));

        // Said last because it is the sentence most likely to be quoted out of the report, and the
        // one this phase is most likely to be misread as contradicting.
        lines.Add("");
        lines.Add(submission.AwaitingAmazon
            ? "This is not a published listing. Amazon has the submission and decides later; ask " +
              $"{AmazonSubmitEndpoints.StatePath} what became of it."
            : "Nothing here claims a listing exists on Amazon.");

        return string.Join(Environment.NewLine, lines);
    }

    public static string Describe(AmazonListingState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var lines = new List<string>
        {
            $"SKU          : {Or(state.Sku, "—")}",
            // "Lookup", not "Status". This one says whether Amazon could be ASKED; the line below
            // says what Amazon answered about the listing. Labelling both "status" puts an "ok"
            // directly above a rejection, which reads as the listing being fine.
            $"Lookup       : {state.Status}",
            $"Amazon says  : {(state.Statuses.Count == 0 ? "nothing yet" : string.Join(", ", state.Statuses))}",
        };

        if (!string.IsNullOrWhiteSpace(state.Asin))     lines.Add($"ASIN         : {state.Asin}");
        if (!string.IsNullOrWhiteSpace(state.ItemName)) lines.Add($"Item         : {state.ItemName}");

        lines.Add("");
        lines.AddRange(Wrap(state.Headline, Rule));

        lines.AddRange(IssueSection(state.Issues));
        lines.AddRange(CallSection(state.Call));

        return string.Join(Environment.NewLine, lines);
    }

    // ── Sections ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Amazon's issues, errors first.
    /// </summary>
    /// <remarks>
    /// Ordered by severity rather than by Amazon's order, because the two kinds mean opposite things
    /// about whether there is a listing: an ERROR is why it did not go up, a WARNING is something to
    /// fix on one that did. A chronological list mixes them and reads as one long list of failures.
    /// </remarks>
    private static IEnumerable<string> IssueSection(List<AmazonSubmissionIssue> issues)
    {
        yield return "";

        if (issues.Count == 0)
        {
            yield return "ISSUES: none. Note that an empty issue list is not a promise — Amazon attaches most " +
                         "of them during processing, after the submission response has been sent.";
            yield break;
        }

        var errors   = issues.Count(i => i.IsError);
        var warnings = issues.Count(i => i.IsWarning);

        yield return $"ISSUES ({errors} error{(errors == 1 ? "" : "s")}, " +
                     $"{warnings} warning{(warnings == 1 ? "" : "s")}, {issues.Count} total)";
        yield return new string('-', Rule);

        foreach (var issue in issues.OrderByDescending(i => i.IsError).ThenByDescending(i => i.IsWarning))
        {
            var mark = issue.IsError ? "[ERROR  ]" : issue.IsWarning ? "[warning]" : "[note   ]";
            yield return $"  {mark} {Or(issue.Code, "uncoded")}" +
                         (issue.AttributeNames.Count > 0 ? $"  {string.Join(", ", issue.AttributeNames)}" : "");

            foreach (var line in Wrap(issue.Message, 66)) yield return "           " + line;
        }
    }

    /// <summary>The request and the response, verbatim.</summary>
    private static IEnumerable<string> CallSection(AmazonCall? call)
    {
        yield return "";

        if (call is null)
        {
            yield return "REQUEST: none was made, so nothing on Amazon was touched.";
            yield break;
        }

        yield return "THE EXCHANGE";
        yield return new string('-', Rule);
        yield return $"  {call.Method} {call.Url}";

        if (!string.IsNullOrWhiteSpace(call.RequestBody))
        {
            yield return "";
            yield return "  Request body:";
            foreach (var line in Indent(Reformat(call.RequestBody))) yield return line;
        }

        yield return "";
        yield return $"  Response: HTTP {(call.HttpStatus?.ToString() ?? "—")}" +
                     (string.IsNullOrWhiteSpace(call.RequestId) ? "" : $"   x-amzn-RequestId: {call.RequestId}");

        if (!string.IsNullOrWhiteSpace(call.ResponseBody))
            foreach (var line in Indent(Reformat(call.ResponseBody))) yield return line;
    }

    // ── Formatting ────────────────────────────────────────────────────────────

    /// <summary>
    /// JSON re-indented for reading, or the text untouched when it is not JSON.
    /// </summary>
    /// <remarks>
    /// Untouched rather than dropped, and that case is real: Amazon's gateway answers some failures
    /// with HTML, and a report that hid anything it could not parse would hide exactly the responses
    /// nobody expected.
    /// </remarks>
    private static string Reformat(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            return node?.ToJsonString(Pretty) ?? body;
        }
        catch (JsonException) { return body; }
    }

    private static IEnumerable<string> Indent(string block) =>
        block.Replace("\r\n", "\n").Split('\n').Select(line => "    " + line);

    private static IEnumerable<string> Wrap(string? text, int width)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line;
                line = "";
            }
            line = line.Length == 0 ? word : line + " " + word;
        }
        if (line.Length > 0) yield return line;
    }

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
