using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// What Anthropic said when the app asked whether this key works, in words the seller can act on.
/// </summary>
/// <remarks>
/// <para>
/// Step 1 of the getting-started path used to tick on a key being <em>saved</em>. Saving a key is
/// not evidence of anything: a key with a character lost in the paste, a key that was revoked, and
/// — much the most common on a brand-new Anthropic account — a key on an account with no credit on
/// it all save exactly as cleanly as a working one. The tester then gets a green tick, spends five
/// minutes on the next two steps, and meets the real failure at the first analysis, several screens
/// away from the field that caused it.
/// </para>
/// <para>
/// So the app asks. One tiny model call answers all three questions at once, because they are the
/// same question to Anthropic: authenticate the key, bill the account, return a token. What that
/// call cannot tell us apart is a wrong key from a broken connection — and those need opposite
/// answers — which is what <see cref="Verdict.Definitive"/> is for. Only a verdict the seller must
/// act on is allowed to untick a step or turn a chip red; "I could not reach Anthropic just now"
/// leaves everything exactly as it was.
/// </para>
/// <para>
/// Pure, and driven off <see cref="FailureTranslator"/> rather than off its own copy of Anthropic's
/// error strings. There is one place in this app that knows what an authentication_error looks like
/// and it is not this file.
/// </para>
/// </remarks>
public static class AiKeyCheck
{
    // ── The states ───────────────────────────────────────────────────────────

    /// <summary>Never asked. The state of every install that has not pressed Test key.</summary>
    public const string Untested = "untested";

    /// <summary>Anthropic authenticated the key and answered. Nothing to do.</summary>
    public const string Works = "works";

    /// <summary>No key is saved at all.</summary>
    public const string Missing = "missing";

    /// <summary>Anthropic refused the key itself — wrong, truncated, or revoked.</summary>
    public const string Rejected = "rejected";

    /// <summary>The key is real; the account behind it cannot pay for a request.</summary>
    public const string NoCredit = "no-credit";

    /// <summary>
    /// The check did not complete — no connection, a rate limit, an Anthropic outage. This says
    /// nothing about the key and must never be recorded over a state that does.
    /// </summary>
    public const string Unreachable = "unreachable";

    public const string KeysUrl = "https://console.anthropic.com/settings/keys";
    public const string BillingUrl = "https://console.anthropic.com/settings/billing";

    /// <summary>Every state this app stores. Anything else read back is treated as untested.</summary>
    public static readonly string[] All = [Untested, Works, Missing, Rejected, NoCredit, Unreachable];

    /// <param name="State">One of the constants above.</param>
    /// <param name="Ok">Anthropic answered. The AI half of the app will work.</param>
    /// <param name="Definitive">
    /// The app knows this key will not work, and knows why. Only these untick step 1 — a failed
    /// check that proves nothing must not take a green tick off a key that has been working.
    /// </param>
    /// <param name="Headline">One line, in the seller's terms.</param>
    /// <param name="WhatToDo">The next action. Never empty.</param>
    /// <param name="Link">Where that action happens, or empty when there is nowhere to go.</param>
    /// <param name="LinkLabel">What to call the link.</param>
    /// <param name="CheckedAt">When this was established, for the dated line on the checklist.</param>
    public sealed record Verdict(
        string State,
        bool Ok,
        bool Definitive,
        string Headline,
        string WhatToDo,
        string Link,
        string LinkLabel,
        DateTimeOffset? CheckedAt);

    /// <summary>The stored spelling of a state, or <see cref="Untested"/> for anything unrecognised.</summary>
    public static string Normalize(string? state) =>
        All.FirstOrDefault(known => string.Equals(known, state?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? Untested;

    /// <summary>
    /// True when this state is the app's own evidence that the key will not work, and therefore
    /// something the seller has to act on. These are the only states allowed to take a tick off
    /// step 1 — <see cref="Unreachable"/> and <see cref="Untested"/> prove nothing and change nothing.
    /// </summary>
    public static bool IsDefinitive(string? state) =>
        Normalize(state) is Rejected or NoCredit or Missing;

    // ── Building a verdict ───────────────────────────────────────────────────

    /// <summary>Anthropic answered the ping.</summary>
    public static Verdict Working(DateTimeOffset? at = null) => Describe(Works, at ?? DateTimeOffset.UtcNow);

    /// <summary>
    /// Turns whatever the model call threw into a verdict, by way of the app's one classifier.
    /// </summary>
    public static Verdict FromFailure(FailureInfo? failure, DateTimeOffset? at = null) =>
        Describe(StateOf(failure), at ?? DateTimeOffset.UtcNow);

    /// <summary>Which state a translated failure means for the key.</summary>
    public static string StateOf(FailureInfo? failure) => failure?.Kind switch
    {
        FailureKind.AiKeyMissing => Missing,
        FailureKind.AiKeyRejected => Rejected,
        FailureKind.AiBilling => NoCredit,

        // Everything else says something about the moment, not about the key. A rate limit proves
        // the key authenticated; a timeout proves nothing at all. Both are "ask again later".
        _ => Unreachable,
    };

    /// <summary>
    /// The full verdict for a state, with no failure in hand — how a stored state is read back on
    /// the next launch, and how the untested case is described.
    /// </summary>
    public static Verdict Describe(string? state, DateTimeOffset? at = null) => Normalize(state) switch
    {
        Works => new Verdict(Works, true, false,
            "Your Claude key works.",
            "Anthropic accepted the key and answered. The AI is ready — nothing else to do here.",
            "", "", at),

        Rejected => new Verdict(Rejected, false, true,
            "Anthropic rejected this key.",
            "Copy the key again from the Anthropic console and paste the whole thing — a key that lost a "
                + "character in the paste, or has since been deleted, fails exactly like this. It starts "
                + "with sk-ant-.",
            KeysUrl, "console.anthropic.com → API keys", at),

        NoCredit => new Verdict(NoCredit, false, true,
            "The key is fine — the Anthropic account has no credit.",
            "Add credit to the Anthropic account and press Test key again. About $5 covers hundreds of "
                + "listings, and nothing about the key itself needs changing.",
            BillingUrl, "console.anthropic.com → Billing", at),

        Missing => new Verdict(Missing, false, true,
            "No Claude key saved yet.",
            "Paste your Anthropic key into the field above and save. Without it nothing auto-fills — this "
                + "key is the AI.",
            KeysUrl, "console.anthropic.com → API keys", at),

        Unreachable => new Verdict(Unreachable, false, false,
            "Couldn't reach Anthropic to check the key.",
            "This says nothing about the key — your connection or Anthropic itself was unavailable. The key "
                + "is still saved. Try again in a moment.",
            "", "", at),

        // Untested, and anything unrecognised read back out of the database.
        _ => new Verdict(Untested, false, false,
            "This key hasn't been tested yet.",
            "Press Test key and Anthropic will say whether it works. It takes a second and costs a fraction "
                + "of a cent.",
            "", "", null),
    };
}
