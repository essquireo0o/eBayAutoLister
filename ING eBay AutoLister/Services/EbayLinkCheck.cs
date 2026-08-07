namespace ING_eBay_AutoLister.Services;

/// <summary>
/// What eBay said when the app asked whether this connection still works, in words the seller can
/// act on — the step-2 twin of <see cref="AiKeyCheck"/>.
/// </summary>
/// <remarks>
/// <para>
/// Step 2 of the getting-started path ticks when this install is <em>holding</em> an eBay token.
/// Holding a token is not evidence that eBay will take a listing. A sign-in that came back through
/// the relay to the wrong port and stored nothing usable, a grant the seller revoked in their eBay
/// account, a refresh token that reached the end of its eighteen months, and a consent screen where
/// the selling permissions were never granted all look identical from disk: something is stored, so
/// the row goes green. The tester then spends five minutes on steps 3-5 and meets the real failure
/// at the publish — the last step of the path, and the one that costs the most to reach.
/// </para>
/// <para>
/// So the app asks. <see cref="ConnectionDoctor.CheckEbayAsync"/> already does exactly this and has
/// since long before onboarding existed: a live token refresh, then an authenticated Sell API call
/// on the token that refresh returned. What was missing was anywhere for its answer to land. It sat
/// behind the Connections card, which runs all four connections, launches two headless browsers and
/// takes the better part of a minute — a "why is this broken" button that nobody presses on their
/// first day, and that no new tester knows exists.
/// </para>
/// <para>
/// This type is the translation layer between that verdict and the checklist: six states, of which
/// four are the app's own evidence that publishing will fail. As in <see cref="AiKeyCheck"/>, the
/// load-bearing distinction is not "did the check succeed" but "does this answer say anything about
/// the connection". Only a <see cref="Verdict.Definitive"/> answer may take a tick off step 2 —
/// eBay being down says nothing about the seller's account, and a tester on a train must not be
/// told their sign-in is broken.
/// </para>
/// <para>
/// Pure, and driven off <see cref="ConnectionState"/> rather than its own second reading of eBay's
/// HTTP statuses. There is one place in this app that decides what a 401 from the Sell API means
/// and it is <see cref="ConnectionDoctor.ClassifyEbay"/>.
/// </para>
/// </remarks>
public static class EbayLinkCheck
{
    // ── The states ───────────────────────────────────────────────────────────

    /// <summary>Never asked. The state of every install that has not run the check.</summary>
    public const string Untested = "untested";

    /// <summary>eBay renewed the sign-in and answered an authenticated call. Publishing will work.</summary>
    public const string Works = "works";

    /// <summary>Nothing to test: the sign-in was never finished, or it was disconnected.</summary>
    public const string NoSession = "no-session";

    /// <summary>The refresh token reached the end of its life and eBay will not renew it.</summary>
    public const string Expired = "expired";

    /// <summary>eBay looked at the stored sign-in and refused it — revoked, or missing permissions.</summary>
    public const string Rejected = "rejected";

    /// <summary>
    /// The app cannot ask for a sign-in at all: no app keys, or it is not serving on the address
    /// eBay has been told to return to. Clicking Connect cannot fix either.
    /// </summary>
    public const string NotConfigured = "not-configured";

    /// <summary>
    /// The check did not complete — no connection, an eBay outage, a token endpoint that timed
    /// out. This says nothing about the account and must never be recorded over a state that does.
    /// </summary>
    public const string Unreachable = "unreachable";

    /// <summary>Every state this app stores. Anything else read back is treated as untested.</summary>
    public static readonly string[] All = [Untested, Works, NoSession, Expired, Rejected, NotConfigured, Unreachable];

    /// <param name="State">One of the constants above.</param>
    /// <param name="Ok">eBay answered. The publishing half of the app will work.</param>
    /// <param name="Definitive">
    /// The app knows this connection will not publish, and knows why. Only these untick step 2 — a
    /// check that proves nothing must not take a green tick off a connection that has been working.
    /// </param>
    /// <param name="Headline">One line, in the seller's terms.</param>
    /// <param name="WhatToDo">The next action. Never empty.</param>
    /// <param name="CheckedAt">When this was established, for the dated line on the checklist.</param>
    public sealed record Verdict(
        string State,
        bool Ok,
        bool Definitive,
        string Headline,
        string WhatToDo,
        DateTimeOffset? CheckedAt);

    /// <summary>The stored spelling of a state, or <see cref="Untested"/> for anything unrecognised.</summary>
    public static string Normalize(string? state) =>
        All.FirstOrDefault(known => string.Equals(known, state?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? Untested;

    /// <summary>
    /// True when this state is the app's own evidence that publishing will fail, and therefore
    /// something the seller has to act on. These are the only states allowed to take a tick off
    /// step 2 — <see cref="Unreachable"/> and <see cref="Untested"/> prove nothing and change nothing.
    /// </summary>
    public static bool IsDefinitive(string? state) =>
        Normalize(state) is Rejected or Expired or NoSession or NotConfigured;

    // ── Building a verdict ───────────────────────────────────────────────────

    /// <summary>Which state the Connection Doctor's verdict means for step 2.</summary>
    /// <remarks>
    /// One case per <see cref="ConnectionState"/>, deliberately exhaustive rather than defaulted:
    /// a state added to the doctor later should be a compile-time decision here, not silently
    /// filed under "eBay was unreachable".
    /// </remarks>
    public static string StateOf(ConnectionState state) => state switch
    {
        ConnectionState.Ok => Works,
        ConnectionState.AuthRejected => Rejected,
        ConnectionState.SessionExpired => Expired,
        ConnectionState.NoSession => NoSession,
        ConnectionState.NotConfigured => NotConfigured,

        // eBay's end, or the network. Says nothing about this seller's account.
        ConnectionState.Unreachable => Unreachable,
        _ => Unreachable,
    };

    /// <summary>Turns the Connection Doctor's eBay answer into a verdict for the checklist.</summary>
    public static Verdict FromCheck(ConnectionCheck check, DateTimeOffset? at = null) =>
        Describe(StateOf(check.State), at ?? DateTimeOffset.UtcNow);

    /// <summary>
    /// The full verdict for a state, with no check in hand — how a stored state is read back on the
    /// next launch, and how the untested case is described.
    /// </summary>
    /// <remarks>
    /// The sentences are this file's, not the doctor's, on purpose. The Connections card explains a
    /// broken connection to somebody who went looking for a diagnosis; this explains it to somebody
    /// on their first day who has not yet been told what a refresh token is. Same verdict, and the
    /// seller sees the same words here whether the check ran a second ago or a fortnight ago.
    /// </remarks>
    public static Verdict Describe(string? state, DateTimeOffset? at = null) => Normalize(state) switch
    {
        Works => new Verdict(Works, true, false,
            "Your eBay connection works.",
            "eBay renewed the sign-in and answered a real call from this app, so publishing will go "
                + "through. Nothing else to do here.",
            at),

        Rejected => new Verdict(Rejected, false, true,
            "eBay is refusing this sign-in.",
            "Click Log into eBay and sign in again — and on eBay's permission screen, accept every "
                + "permission it asks for. A sign-in that skipped the selling ones stores perfectly and "
                + "then cannot list anything.",
            at),

        Expired => new Verdict(Expired, false, true,
            "Your eBay sign-in has expired.",
            "eBay's sign-ins run out after about eighteen months and it will not renew this one. Click "
                + "Log into eBay to sign in again — nothing you have made is lost.",
            at),

        NoSession => new Verdict(NoSession, false, true,
            "The eBay sign-in didn't finish.",
            "Click Log into eBay, sign in on eBay's own page, and let the window come back to this app. "
                + "A sign-in that ends on eBay's site, or in a tab you closed, never reaches the app.",
            at),

        NotConfigured => new Verdict(NotConfigured, false, true,
            "This app can't start an eBay sign-in yet.",
            $"Either the eBay app keys are missing, or this app isn't serving on {AppPaths.BaseUrl} — the "
                + "one address eBay is told to send you back to. Restart ING AutoLister, then try Log into "
                + "eBay again.",
            at),

        Unreachable => new Verdict(Unreachable, false, false,
            "Couldn't reach eBay to check the connection.",
            "This says nothing about your account — eBay's end or this machine's connection was "
                + "unavailable. Your sign-in is still saved. Try again in a moment.",
            at),

        // Untested, and anything unrecognised read back out of the database.
        _ => new Verdict(Untested, false, false,
            "This connection hasn't been tested yet.",
            "Press Test connection and the app will renew the sign-in and make one real call to eBay. It "
                + "takes a couple of seconds and costs nothing.",
            null),
    };
}
