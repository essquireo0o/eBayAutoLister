using System.Text.Encodings.Web;
using System.Text.Json;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Renders a filled Amazon listing as plain text: what is answered, what is not, and the payload.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <see cref="AmazonAttributeReport"/>, and it exists for the same reason. The
/// question this phase has to answer — "would this draft go up on Amazon, and if not, what is
/// stopping it?" — is one a person answers by reading, and reading it as JSON is a chore.
/// </para>
/// <para>
/// The sections are ordered the way the work is: what is blocked, then what is done, then what is
/// merely available. A report that opened with 140 filled optional attributes would bury the two
/// fields that decide whether anything can be submitted.
/// </para>
/// </remarks>
public static class AmazonListingFillReport
{
    /// <summary>
    /// Indented, and without the default encoder's escaping.
    /// </summary>
    /// <remarks>
    /// The relaxed encoder is what makes the payload readable. The strict one escapes every plus
    /// sign, ampersand and quote into its six-character unicode form, so a product genuinely called
    /// "NerdQaxe++" arrives in the report unrecognisable — and a report nobody can read is not a
    /// report. Safe here because this is served as <c>text/plain</c> and never embedded in a page:
    /// that escaping exists to stop a value closing a script tag, and there is no script tag. The
    /// payload that goes to Amazon is serialized separately, by the caller, with its own encoder.
    /// </remarks>
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Describe(AmazonListingFill fill, bool includePayload = true)
    {
        ArgumentNullException.ThrowIfNull(fill);

        var lines = new List<string>
        {
            $"Draft        : {fill.SourceTitle}",
            $"Product type : {fill.ProductType}" +
                (string.IsNullOrWhiteSpace(fill.DisplayName) ? "" : $"  ({fill.DisplayName})"),
            $"Marketplace  : {fill.MarketplaceId}   Locale: {fill.Locale}",
            $"Status       : {fill.Status}" +
                (string.IsNullOrWhiteSpace(fill.Message) ? "" : $" — {fill.Message}"),
            "",
            fill.Headline,
        };

        if (!string.IsNullOrWhiteSpace(fill.SandboxNotice))
        {
            lines.Add("");
            lines.Add("SANDBOX: " + fill.SandboxNotice);
        }

        if (fill.Attributes.Count == 0) return string.Join(Environment.NewLine, lines);

        // Required, in the order Amazon listed them, each marked. This is the section the acceptance
        // question is about, so it is one block rather than a filled list and a missing list — a
        // seller checking their work wants the whole requirement, ticked and unticked together.
        var required = fill.Attributes.Where(a => a.Required).ToList();
        lines.Add("");
        lines.Add($"REQUIRED ATTRIBUTES ({fill.RequiredFilledCount} of {required.Count} filled)");
        lines.Add(new string('-', 78));
        if (required.Count == 0) lines.Add("  (none)");
        foreach (var attribute in required) lines.AddRange(Lines(attribute));

        var conditional = fill.Attributes
            .Where(a => a.ConditionallyRequired && !a.Required).ToList();

        if (conditional.Count > 0)
        {
            var met = fill.Choices.Count(c => c.Satisfied);
            lines.Add("");
            lines.Add($"EITHER/OR REQUIREMENTS ({met} of {fill.Choices.Count} satisfied)");
            lines.Add(new string('-', 78));

            foreach (var choice in fill.Choices)
                lines.Add($"  {(choice.Satisfied ? "[ok]     " : "[BLOCKED]")} {choice.Note}");

            lines.Add("");
            foreach (var attribute in conditional) lines.AddRange(Lines(attribute));
        }

        var optionalFilled = fill.Attributes
            .Where(a => !a.Required && !a.ConditionallyRequired && a.IsFilled).ToList();

        lines.Add("");
        lines.Add($"OPTIONAL ATTRIBUTES FILLED FROM THE DRAFT ({optionalFilled.Count})");
        lines.Add(new string('-', 78));
        if (optionalFilled.Count == 0) lines.Add("  (none)");
        foreach (var attribute in optionalFilled) lines.AddRange(Lines(attribute));

        var optionalEmpty = fill.Attributes
            .Count(a => !a.Required && !a.ConditionallyRequired && !a.IsFilled);
        lines.Add($"  … and {optionalEmpty} optional attribute{(optionalEmpty == 1 ? "" : "s")} " +
                  $"the draft says nothing about.");

        if (includePayload)
        {
            lines.Add("");
            lines.Add($"PAYLOAD — the \"attributes\" object of a Listings Items submission " +
                      $"({fill.Payload.Count} attribute{(fill.Payload.Count == 1 ? "" : "s")})");
            lines.Add(new string('-', 78));
            lines.Add(fill.Payload.ToJsonString(Pretty));
        }

        lines.Add("");
        lines.Add(fill.CanSubmit
            ? "Nothing here is submitted. This phase builds the payload; sending it is the next one."
            : "This listing cannot be submitted, and no value was invented to make it look as though " +
              "it could.");

        return string.Join(Environment.NewLine, lines);
    }

    private static IEnumerable<string> Lines(AmazonFilledAttribute attribute)
    {
        yield return $"  {Mark(attribute.State)} {attribute.Name,-42} {Value(attribute)}";

        if (attribute.IsFilled && !string.IsNullOrWhiteSpace(attribute.Source))
            yield return $"           from {attribute.Source}";

        if (!string.IsNullOrWhiteSpace(attribute.Note))
            foreach (var line in Wrap(attribute.Note, 66))
                yield return "           " + line;

        foreach (var extra in attribute.Values.Skip(1))
            yield return $"           + {Clip(extra, 62)}";
    }

    private static string Value(AmazonFilledAttribute attribute)
    {
        if (attribute.Values.Count == 0) return "—";

        var text = Clip(attribute.Values[0], 30);
        return attribute.Values.Count > 1 ? $"{text}  (+{attribute.Values.Count - 1})" : text;
    }

    /// <summary>
    /// The state as something scannable down the left edge.
    /// </summary>
    /// <remarks>
    /// Deliberately not a tick and a cross. There are five outcomes here and three of them are not
    /// "missing" — a field satisfied by its alternative, a value Amazon's list does not contain, and
    /// a value too long to send all read as failures under two symbols, and each needs a different
    /// thing done about it.
    /// </remarks>
    private static string Mark(string state) => state switch
    {
        AmazonFillState.Filled                => "[filled ]",
        AmazonFillState.MissingRequired       => "[MISSING]",
        AmazonFillState.MissingConditional    => "[missing]",
        AmazonFillState.SatisfiedByAlternative => "[n/a    ]",
        AmazonFillState.InvalidValue          => "[REJECT ]",
        AmazonFillState.TooLong               => "[TOOLONG]",
        _                                     => "[empty  ]",
    };

    private static string Clip(string text, int width)
    {
        var flat = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length <= width ? flat : flat[..(width - 1)] + "…";
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
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
}
