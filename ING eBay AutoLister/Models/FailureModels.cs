namespace ING_eBay_AutoLister.Models;

/// <summary>
/// Which subsystem a call belongs to. Classification is domain-scoped on purpose.
/// </summary>
/// <remarks>
/// The same words mean different things in different places. "429" in an Anthropic message is a
/// rate limit; "429" in an eBay rejection is very often a price. Passing the domain in means the
/// translator never has to guess which one it is looking at, so a listing priced at $429.00 can
/// never be reported to the seller as "the AI is rate limited".
/// </remarks>
public enum FailureDomain
{
    Ai,
    Ebay,
    Photos,
    Research,
    Storage,
    App,
}

/// <summary>
/// What actually went wrong, at the granularity the seller's next action depends on.
/// </summary>
public enum FailureKind
{
    // Transient — the same request will probably work shortly.
    Timeout,
    Network,
    RateLimited,
    Overloaded,
    UpstreamServerError,
    AiUnreadableReply,
    StorageBusy,

    // Permanent — retrying identical input changes nothing.
    AiKeyMissing,
    AiKeyRejected,
    AiBilling,
    InputTooLarge,
    EbayNotConnected,
    EbayAuthExpired,
    EbayPoliciesMissing,
    EbayRejected,
    NotFound,
    BadInput,
    DiskFull,
    ToolMissing,
    Unknown,
}

/// <summary>
/// One failure, described the way the UI needs it: a headline, what happened, what to do about it,
/// whether a retry is worth offering, and the raw text kept as evidence.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Retryable"/> is the load-bearing field. A Retry button on a permanent failure trains
/// sellers to click it on every failure, which is how a wrong API key turns into five wasted
/// minutes; and no Retry button on a transient one throws away work that would have succeeded on
/// the next attempt. Both directions cost the seller real money, so the distinction is decided once
/// here rather than at each call site.
/// </para>
/// <para>
/// <see cref="Technical"/> is never the primary message but is always carried. Sellers forward it
/// when asking for help, and a support conversation that starts with eBay's own error text is a
/// much shorter one.
/// </para>
/// </remarks>
public sealed record FailureInfo
{
    public FailureKind Kind { get; init; } = FailureKind.Unknown;
    public FailureDomain Domain { get; init; } = FailureDomain.App;

    /// <summary>Short, plain, and specific — shown as the panel heading.</summary>
    public string Headline { get; init; } = "Something went wrong";

    /// <summary>One sentence of cause, in the seller's terms rather than the API's.</summary>
    public string WhatHappened { get; init; } = "";

    /// <summary>The next action. Never empty — "wait and retry" is an action.</summary>
    public string WhatToDo { get; init; } = "";

    public bool Retryable { get; init; }

    /// <summary>Honoured from a Retry-After header when the service supplied one.</summary>
    public int? RetryAfterSeconds { get; init; }

    /// <summary>Raw error text, kept verbatim as evidence.</summary>
    public string Technical { get; init; } = "";

    /// <summary>
    /// Machine-readable hint the UI renders as a button: <c>ai-key</c>, <c>connect-ebay</c>,
    /// <c>ebay-policies</c>, <c>open-logs</c>, or empty for none.
    /// </summary>
    public string FixAction { get; init; } = "";

    /// <summary>How many times the operation was actually attempted before giving up.</summary>
    public int Attempts { get; init; } = 1;

    /// <summary>
    /// True when the seller's own content survived the failure — nothing they typed or generated
    /// was discarded. Every path in this app is meant to be able to say yes.
    /// </summary>
    public bool WorkPreserved { get; init; } = true;
}

/// <summary>
/// A failure that has already been classified. Thrown by the resilience layer so an endpoint can
/// report the translated failure rather than re-deriving it from a raw exception.
/// </summary>
public sealed class AppFailureException(FailureInfo failure)
    : Exception(failure.Headline + " — " + failure.WhatHappened)
{
    public FailureInfo Failure { get; } = failure;
}
