namespace ING_eBay_AutoLister.Models;

// ── "Will this site let itself be embedded?" (see Services/FrameEmbedPolicy.cs) ────────────────
// The WhatsNot panel puts a live-selling feed in an <iframe>. A site that sends X-Frame-Options or
// a CSP frame-ancestors directive is refused by the browser, and the browser tells the PAGE
// nothing about it — the frame just sits there, blank, looking exactly like a slow load or a bug in
// this app. That silence is the whole problem this check exists to end: the app asks the site for
// its headers itself and says, in words, which of the three it is.

/// <summary>The four answers. Spelled once so the endpoint, the tests and the CSS agree.</summary>
public static class FrameEmbedStatuses
{
    /// <summary>Nothing in the site's headers refuses framing. It should render.</summary>
    public const string Allowed = "allowed";
    /// <summary>The site refuses to be framed. The frame will stay blank, whatever it is pointed at.</summary>
    public const string Refused = "refused";
    /// <summary>The check couldn't reach a verdict — the site turned the app's request away, or it
    /// timed out. Different from "allowed": the frame may still work, and may still be blank.</summary>
    public const string Unknown = "unknown";
    /// <summary>Not an address this panel can load at all.</summary>
    public const string Invalid = "invalid";
}

/// <summary>One address, asked whether a browser will let this app frame it.</summary>
public sealed class FrameEmbedCheck
{
    /// <summary>See <see cref="FrameEmbedStatuses"/>.</summary>
    public string Status { get; set; } = FrameEmbedStatuses.Unknown;

    /// <summary>The address as it was actually asked about, after the scheme was filled in.</summary>
    public string Url { get; set; } = "";
    public string Host { get; set; } = "";

    /// <summary>One line for the status strip, in this site's own name.</summary>
    public string Headline { get; set; } = "";

    /// <summary>What to do about it. On a refusal this is the sentence that stops the seller
    /// thinking the app is broken, so it says both "the site did this" and "here is the way in".</summary>
    public string Detail { get; set; } = "";

    /// <summary>The header that decided it, verbatim — e.g. <c>X-Frame-Options: DENY</c>. Empty when
    /// no header was the reason. Shown because a claim about somebody else's server should carry
    /// its evidence.</summary>
    public string Header { get; set; } = "";

    /// <summary>headers | known | unreachable | validation — where the verdict came from. A verdict
    /// read off a live response outranks one remembered from a list, and the screen says which.</summary>
    public string Source { get; set; } = "";

    /// <summary>HTTP status the probe got back, when it got one.</summary>
    public int? HttpStatus { get; set; }

    public long ElapsedMs { get; set; }
}
