namespace ING_eBay_AutoLister.Services;

/// <summary>
/// What survives of a JSON array that stopped before it finished.
/// </summary>
/// <remarks>
/// <para>
/// A model reply that runs out of output tokens ends somewhere in the middle of an object and never
/// closes its bracket. A strict reader finds no <c>]</c>, throws, and the caller loses every
/// complete object that <i>did</i> arrive — on a board of sixty items, sixty prices discarded
/// because the sixtieth was half-written.
/// </para>
/// <para>
/// This keeps everything that closed cleanly. It is a scanner rather than a regex because the only
/// way to know a <c>}</c> ends an object is to have tracked whether it is inside a string and how
/// deep the braces go: a title like <c>"6\" pipe, {new}"</c> is full of characters that look like
/// structure and are not.
/// </para>
/// </remarks>
public static class JsonSalvage
{
    /// <summary>
    /// The longest prefix of the array in <paramref name="text"/> that is valid JSON, closed off.
    /// Returns an empty string when not one object completed.
    /// </summary>
    public static string CompleteObjects(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var start = text.IndexOf('[');
        if (start < 0) return "";

        int depth = 0, lastComplete = -1;
        bool inString = false, escaped = false;

        for (var i = start + 1; i < text.Length; i++)
        {
            var ch = text[i];

            if (inString)
            {
                // An escape consumes whatever follows it, including a quote or another backslash.
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;
                continue;
            }

            switch (ch)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) lastComplete = i;    // an object closed cleanly here
                    break;
                case ']' when depth == 0:
                    // The array closed on its own — nothing was lost, hand back what is there.
                    return text[start..(i + 1)];
            }
        }

        return lastComplete < 0 ? "" : string.Concat(text.AsSpan(start, lastComplete - start + 1), "]");
    }
}
