using System.Text.Json;
using System.Text.Json.Serialization;

namespace ING_eBay_AutoLister.Services;

// ── The sign-in as a thing with a state ───────────────────────────────────────
// eBay sign-in is four hops — this app, eBay's consent page, the PHP relay on inglisting.com, and
// this app again — and three of them happen in a browser tab this process does not control. Every
// one of those hops can end without anybody calling back: the seller closes the tab on the consent
// page, the relay is down, the pickup is slow, the link gets opened twice from history. Before
// this, all of them looked identical from inside the app — nothing had happened, and nothing ever
// would — so the UI sat on "connecting" indefinitely and the log said nothing.
//
// Everything in this file is pure so EbaySignInTests can pin each of those endings without a
// browser, a relay or a socket.

/// <summary>Where an eBay sign-in has got to. <see cref="Failed"/> always carries a reason.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EbaySignInStage
{
    /// <summary>Nothing has been started since the app came up.</summary>
    Idle,
    /// <summary>An authorization URL was handed out; the seller is on eBay's consent page.</summary>
    AwaitingConsent,
    /// <summary>eBay came back and the tokens are being collected from the relay.</summary>
    Exchanging,
    /// <summary>Tokens are stored.</summary>
    Connected,
    /// <summary>It ended, and not with tokens.</summary>
    Failed
}

/// <param name="Code">Stable machine-readable cause — also what the redirect carries as ebay_error.</param>
/// <param name="Message">One sentence for the seller saying what actually happened.</param>
/// <param name="NextAction">The single thing to do about it.</param>
public sealed record EbaySignInStatus(
    EbaySignInStage Stage,
    string Code,
    string Message,
    string NextAction,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? UpdatedAt = null,
    string? Detail = null)
{
    public static readonly EbaySignInStatus Idle = new(
        EbaySignInStage.Idle, "idle",
        "No eBay sign-in has been started since the app was launched.",
        "Open Settings → eBay and click Connect eBay Account.");

    /// <summary>
    /// Ages a pending sign-in out. A seller who closes the consent tab never comes back, and the
    /// app has no way to hear about it — so after <see cref="EbayOAuthSessionLedger.Lifetime"/>
    /// with nothing arriving, "waiting" becomes an answer instead of a state it never leaves.
    /// </summary>
    public EbaySignInStatus AgedAt(DateTimeOffset now)
    {
        if (Stage is not (EbaySignInStage.AwaitingConsent or EbaySignInStage.Exchanging)) return this;
        if (StartedAt is not { } started || now - started <= EbayOAuthSessionLedger.Lifetime) return this;

        return this with
        {
            Stage = EbaySignInStage.Failed,
            Code = "sign_in_abandoned",
            Message = $"The eBay sign-in started {(now - started).TotalMinutes:0} minutes ago never came back — " +
                      "the browser tab was most likely closed before eBay's consent screen was finished.",
            NextAction = "Click Connect eBay Account again and complete the consent screen without closing the tab.",
            UpdatedAt = now,
        };
    }
}

// ── Pending sign-in sessions ──────────────────────────────────────────────────

/// <summary>Verdict on the <c>session</c> value the relay hands back.</summary>
public enum EbaySessionCheck
{
    /// <summary>Issued by this process, inside its lifetime, not yet used.</summary>
    Valid,
    /// <summary>Never issued here — a forged or stale link, or the app was restarted mid-sign-in.</summary>
    Unknown,
    /// <summary>Issued here, but too long ago to still be in flight.</summary>
    Expired,
    /// <summary>Already completed once. Re-opening the same link is the usual cause.</summary>
    AlreadyUsed
}

/// <summary>
/// The sign-in sessions this process has handed out, and what became of them.
/// </summary>
/// <remarks>
/// This replaces a single <c>PendingOAuthSession</c> string, which had two failure modes that both
/// ended as "state_mismatch" — a message that reads like tampering and told the seller nothing.
/// Asking for the auth URL twice (open Settings, go back, open it again) overwrote the first
/// session, so finishing the first sign-in was rejected; and coming back to a link a second time
/// was rejected identically, even though the first attempt had actually worked.
/// </remarks>
public sealed class EbayOAuthSessionLedger
{
    /// <summary>How long a sign-in may stay in flight. Generous: eBay's consent screen can involve
    /// a password, 2FA and a permissions review.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>Recent sessions kept. Small on purpose — this is a short queue, not a store.</summary>
    private const int Capacity = 8;

    private sealed record Entry(string Id, DateTimeOffset IssuedAt, DateTimeOffset? ConsumedAt);

    private readonly object _sync = new();
    private readonly List<Entry> _entries = [];

    public void Issue(string id, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_sync)
        {
            _entries.RemoveAll(e => e.Id == id);
            _entries.Add(new Entry(id, now, null));
            if (_entries.Count > Capacity) _entries.RemoveRange(0, _entries.Count - Capacity);
        }
    }

    public EbaySessionCheck Check(string? id, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(id)) return EbaySessionCheck.Unknown;
        lock (_sync)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id);
            if (entry is null) return EbaySessionCheck.Unknown;
            if (entry.ConsumedAt is not null) return EbaySessionCheck.AlreadyUsed;
            return now - entry.IssuedAt > Lifetime ? EbaySessionCheck.Expired : EbaySessionCheck.Valid;
        }
    }

    /// <summary>Marks a session finished. Idempotent — a second call is what <see cref="EbaySessionCheck.AlreadyUsed"/> reports.</summary>
    public void Consume(string id, DateTimeOffset now)
    {
        lock (_sync)
        {
            var index = _entries.FindIndex(e => e.Id == id);
            if (index >= 0 && _entries[index].ConsumedAt is null)
                _entries[index] = _entries[index] with { ConsumedAt = now };
        }
    }

    /// <summary>True while at least one issued session could still legitimately come back.</summary>
    public bool HasPending(DateTimeOffset now)
    {
        lock (_sync)
            return _entries.Any(e => e.ConsumedAt is null && now - e.IssuedAt <= Lifetime);
    }
}

// ── Building the authorization URL ────────────────────────────────────────────

/// <summary>Why an authorization URL cannot be built, in the seller's terms.</summary>
public sealed record EbayAuthUrlProblem(string Code, string Reason, string NextAction);

/// <summary>
/// Everything that has to line up before the sign-in URL is worth handing out.
/// </summary>
/// <remarks>
/// The three parties have to agree on one redirect: eBay redirects to the RuName registered against
/// the Client ID, the RuName's accepted URL is the relay on inglisting.com, and the relay redirects
/// to <c>http://localhost:9332</c>. Get any of them wrong and the URL still opens, the consent
/// screen still appears, and the failure only shows up after the seller has approved everything —
/// as a browser error page eBay's own wording blames on the application. A URL that cannot come
/// back is worse than no URL, so this returns the problem instead.
/// </remarks>
public static class EbayAuthUrlCheck
{
    /// <summary>The permissions the app asks for. One string, in one place, so the URL that is
    /// built and the set that is documented cannot drift apart.</summary>
    public static readonly string[] Scopes =
    [
        "https://api.ebay.com/oauth/api_scope",
        "https://api.ebay.com/oauth/api_scope/sell.inventory",
        "https://api.ebay.com/oauth/api_scope/sell.account",
        "https://api.ebay.com/oauth/api_scope/sell.fulfillment",
    ];

    /// <summary>
    /// Send Offer to Interested Buyers. Asked for only when a deployment turns it on — see
    /// <see cref="EbayScopeOptions"/> — and this is the expensive part: <b>eBay rejects the entire
    /// authorization request with <c>invalid_scope</c> if a keyset is not enabled for this one.</b>
    /// </summary>
    /// <remarks>
    /// Not "the offers screen stops working": every sign-in stops working. Measured against the
    /// live production keyset on 2026-08-13 — the four scopes above reach eBay's real consent
    /// screen, and adding this one turns the same URL into
    /// <c>auth2.ebay.com/oauth2/errorOauth?errorId=invalid_scope</c> before the seller sees
    /// anything at all. There is no way to detect that from here: whether a keyset has the
    /// permission is a fact about the eBay developer account, so it has to be declared.
    /// <para>
    /// Off by default, therefore, because the cost of the two answers is not symmetric. Off when it
    /// was available loses one feature, which already fails on purpose and says why
    /// (<see cref="EbayPermissionException"/>). On when it was not available loses every sign-in,
    /// with an error that names nothing a seller can act on.
    /// </para>
    /// </remarks>
    public const string NegotiationScope = "https://api.ebay.com/oauth/api_scope/sell.negotiation";

    /// <summary>The scopes a sign-in asks for, given whether the keyset has Send Offer enabled.</summary>
    public static string[] ScopesFor(bool includeNegotiation) =>
        includeNegotiation ? [.. Scopes, NegotiationScope] : Scopes;

    public static string ScopeParameter => ScopeParameterFor(false);

    public static string ScopeParameterFor(bool includeNegotiation) =>
        string.Join(" ", ScopesFor(includeNegotiation));

    /// <param name="bindingProblem">
    /// <see cref="ServerBinding.Problem"/> — non-null when this app is not on the port the relay
    /// redirects to. Only fatal for the relay flow; a sandbox RuName points wherever its owner set it.
    /// </param>
    /// <param name="redirectUri">
    /// The URL this deployment needs registered against the RuName, named in the "no RuName" advice
    /// so that a hosted deployment does not tell its seller to register the desktop's relay. Null
    /// means the desktop default. See <see cref="EbayRedirect"/> for why the app has to be told.
    /// </param>
    public static EbayAuthUrlProblem? Check(
        string? clientId, string? clientSecret, string? ruName, bool sandbox, string? bindingProblem,
        string? redirectUri = null)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return new("no_client_id",
                "The eBay app Client ID hasn't been entered, so there is no application for eBay to show a consent screen for.",
                "Open Settings → eBay → Advanced and paste the Client ID from your eBay developer account.");

        if (string.IsNullOrWhiteSpace(clientSecret))
            return new("no_client_secret",
                "The eBay app Client Secret hasn't been entered. Sign-in would appear to work and then fail at the point tokens are issued, and nothing could be refreshed afterwards.",
                "Open Settings → eBay → Advanced and paste the Client Secret from your eBay developer account.");

        if (string.IsNullOrWhiteSpace(ruName))
            return new("no_runame",
                "No eBay RuName is configured. eBay sends the seller back to the redirect registered against the RuName, so without one the sign-in URL has nowhere to return to.",
                "Open Settings → eBay → Advanced and paste the RuName whose accepted URL is " +
                $"{(string.IsNullOrWhiteSpace(redirectUri) ? EbayRedirect.DesktopDefault : redirectUri)}.");

        // Sandbox never goes through the relay — its RuName points at whatever the developer
        // registered — so the local port is not part of that round trip.
        if (!sandbox && bindingProblem is not null)
            return new("wrong_port", bindingProblem,
                $"Restart ING AutoLister so it is serving on {AppPaths.BaseUrl}, then connect eBay again.");

        return null;
    }

    /// <summary>Builds the consent URL. Callers must have cleared <see cref="Check"/> first.</summary>
    public static string Build(string authBaseUrl, string clientId, string redirectUri, string state,
        bool includeNegotiationScope = false) =>
        $"{authBaseUrl}?client_id={Uri.EscapeDataString(clientId)}" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        $"&response_type=code&scope={Uri.EscapeDataString(ScopeParameterFor(includeNegotiationScope))}" +
        $"&state={Uri.EscapeDataString(state)}";
}

/// <summary>
/// Which optional eBay permissions this deployment's keyset is actually allowed to ask for.
/// </summary>
/// <remarks>
/// One setting, and it exists because eBay answers "you asked for a permission you do not have" by
/// refusing the whole sign-in rather than that permission. See
/// <see cref="EbayAuthUrlCheck.NegotiationScope"/> for the measurement.
/// </remarks>
public sealed class EbayScopeOptions
{
    /// <summary>Configuration key. As an environment variable: <c>Ebay__RequestNegotiationScope</c>.</summary>
    public const string NegotiationSetting = "Ebay:RequestNegotiationScope";

    /// <summary>What every build does unless told otherwise: ask only for what listing needs.</summary>
    public static EbayScopeOptions Default { get; } = new();

    /// <summary>True only where the eBay keyset has Send Offer to Interested Buyers enabled.</summary>
    public bool IncludeNegotiation { get; init; }

    public static EbayScopeOptions FromConfiguration(IConfiguration configuration) => new()
    {
        IncludeNegotiation =
            bool.TryParse(configuration[NegotiationSetting], out var include) && include,
    };
}

// ── Telling the relay which build started the sign-in ─────────────────────────

/// <summary>
/// Which deployment the relay hands the browser back to, said in the only field eBay preserves.
/// </summary>
/// <remarks>
/// <para>
/// eBay returns the seller to whatever URL is registered against the RuName, and that is the PHP
/// relay on inglisting.com for every build of this app — the hosted one included, because the
/// registration lives in the owner's eBay developer console and there is exactly one of it. The
/// relay then has to send the browser on to the app that started the sign-in, and it has no way to
/// tell them apart: the desktop build is <c>http://localhost:9332</c> on somebody's own PC and the
/// hosted build is <c>https://app.inglisting.com</c>, and both arrive at the relay looking
/// identical. Before this, the relay's last hop was hardcoded to localhost, so a sign-in started on
/// the hosted site ended on a dead tab pointing at a port on the seller's own machine.
/// </para>
/// <para>
/// <c>state</c> is the one thing eBay round-trips untouched, so it carries the answer: the 32 hex
/// characters the app already generates, plus a single letter naming the destination. The relay
/// matches <c>^([0-9a-f]{32})([a-z]?)$</c>, strips the letter, and looks the destination up in an
/// allow-list — <c>d</c> (or nothing at all) for the desktop, <c>h</c> for the hosted site. An
/// allow-list and not a return_url parameter, deliberately: a relay that redirects wherever it is
/// told is an open redirect, and this one is carrying a freshly minted eBay session.
/// </para>
/// <para>
/// <b>The suffix goes on the wire and nowhere else.</b> The session id this app issues, records in
/// its <see cref="EbayOAuthSessionLedger"/> and later presents to the relay's pickup endpoint stays
/// bare, because the relay stripped the letter before it stored anything under that key —
/// <c>/api/ebay/pickup</c> refuses any session that is not exactly 32 hex characters. So the suffix
/// is added at the moment the URL is built and removed from anything that comes back; see
/// <see cref="SessionFrom"/>, which exists because eBay's <i>direct</i> callback echoes the state
/// verbatim, suffix and all.
/// </para>
/// <para>
/// A desktop build appends nothing and is byte-for-byte the state it has always sent, which is what
/// keeps every already-installed copy working against the same relay.
/// </para>
/// </remarks>
public sealed class EbayRelayReturn
{
    /// <summary>What the desktop build appends: nothing. The relay's default is localhost:9332.</summary>
    public const string DesktopSuffix = "";

    /// <summary>The relay's allow-list entry for <c>https://app.inglisting.com/api/ebay/finish</c>.</summary>
    public const string HostedSuffix = "h";

    public static EbayRelayReturn Desktop { get; } = new(DesktopSuffix);
    public static EbayRelayReturn Hosted  { get; } = new(HostedSuffix);

    /// <summary>Picks the pair. <c>hosted</c> is <see cref="HostedAuth.IsHostedBuild"/> in Program.cs.</summary>
    public static EbayRelayReturn For(bool hosted) => hosted ? Hosted : Desktop;

    private EbayRelayReturn(string suffix) => Suffix = suffix;

    /// <summary>The letter appended to the state, or empty for the desktop build.</summary>
    public string Suffix { get; }

    /// <summary>The <c>state</c> to send eBay for a session this app issued.</summary>
    public string StateFor(string session) => session + Suffix;

    /// <summary>
    /// The session id inside a state that came back — the bare 32 hex, whatever letter is on it.
    /// </summary>
    /// <remarks>
    /// Applied to every inbound state rather than only the hosted one. The relay strips the suffix
    /// itself, so the <c>?session=</c> on <c>/api/ebay/finish</c> is already bare; but eBay's direct
    /// callback hands back what it was given, and a state that still has its letter would miss the
    /// ledger entry it belongs to and retire nothing. Anything that is not this shape is returned
    /// untouched, so a forged or stale value still fails the ledger check on its own merits.
    /// </remarks>
    public static string SessionFrom(string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return state ?? "";

        var trimmed = state.Trim();
        var match = System.Text.RegularExpressions.Regex.Match(trimmed, "^([0-9a-fA-F]{32})[a-zA-Z]?$");
        return match.Success ? match.Groups[1].Value : trimmed;
    }
}

// ── Collecting the tokens from the relay ──────────────────────────────────────

/// <summary>What one call to the relay's pickup endpoint amounted to.</summary>
public enum EbayPickupOutcome
{
    /// <summary>Tokens are there.</summary>
    Ready,
    /// <summary>The relay answered, but hasn't stored the tokens yet. Worth asking again.</summary>
    NotReady,
    /// <summary>The relay refused this pickup and will keep refusing it.</summary>
    Rejected,
    /// <summary>The relay is up but broken (5xx). Not the seller's account.</summary>
    Unavailable,
    /// <summary>Nothing answered at all — DNS, connection refused, or a timeout.</summary>
    Unreachable,
    /// <summary>A success status carrying something that isn't the documented token payload.</summary>
    Malformed
}

public sealed record EbayRelayTokens(
    string AccessToken, string RefreshToken, int ExpiresIn, int RefreshTokenExpiresIn, string TokenType);

/// <summary>
/// Reads the relay's pickup response. Separated from the polling so the interesting part — which
/// answers mean "ask again" and which mean "stop" — is testable without a network.
/// </summary>
/// <remarks>
/// The pickup is a race the app usually wins: eBay redirects the browser here at the same moment
/// the relay is writing the tokens down, so the first read legitimately finds nothing. That is why
/// 404 and 202/425 are <see cref="EbayPickupOutcome.NotReady"/> rather than failures — but only
/// until the deadline, because "not ready" forever is the hang this replaced.
/// </remarks>
public static class EbayRelayPickup
{
    /// <summary>Ceiling on one pickup request.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Ceiling on the whole pickup, retries included. The browser is sitting on a blank
    /// tab for all of it, so this is as long as anyone will wait for an answer either way.</summary>
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Gap between attempts while the relay says "not yet".</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>The relay's pickup endpoint. One relay for every build — see <see cref="EbayRelayReturn"/>.</summary>
    public const string Endpoint = "https://inglisting.com/api/ebay/pickup/";

    /// <summary>
    /// Where the tokens for one sign-in are claimed from.
    /// </summary>
    /// <remarks>
    /// The session is normalised through <see cref="EbayRelayReturn.SessionFrom"/> because the relay
    /// stores the tokens under the <i>bare</i> 32 hex and its pickup endpoint rejects anything else
    /// outright — a state that still carried its hosted <c>h</c> would be answered HTTP 400 "Invalid
    /// session ID", reported as a relay that refused the pickup, and the sign-in would fail at the
    /// last hop with nothing about it naming the letter that caused it.
    /// </remarks>
    public static string Url(string session, string pickup) =>
        $"{Endpoint}?session={Uri.EscapeDataString(EbayRelayReturn.SessionFrom(session))}" +
        $"&pickup={Uri.EscapeDataString(pickup)}";

    /// <param name="status">Null when nothing answered.</param>
    public static EbayPickupOutcome Classify(int? status, string? body)
    {
        if (status is null) return EbayPickupOutcome.Unreachable;
        if (status is >= 500) return EbayPickupOutcome.Unavailable;

        // The relay has not written the tokens down yet — the redirect beat it here.
        if (status is 404 or 202 or 204 or 425) return EbayPickupOutcome.NotReady;
        if (status is >= 400) return EbayPickupOutcome.Rejected;

        if (string.IsNullOrWhiteSpace(body)) return EbayPickupOutcome.NotReady;

        return TryReadTokens(body, out var tokens)
            ? string.IsNullOrWhiteSpace(tokens!.AccessToken) ? EbayPickupOutcome.NotReady : EbayPickupOutcome.Ready
            : EbayPickupOutcome.Malformed;
    }

    /// <summary>
    /// Pulls the token fields out of the relay's JSON. Absent numbers fall back to eBay's own
    /// documented lifetimes (2 hours / 18 months) rather than to zero, which would record "no
    /// expiry" and stop the proactive refresh from ever firing.
    /// </summary>
    public static bool TryReadTokens(string? body, out EbayRelayTokens? tokens)
    {
        tokens = null;
        if (string.IsNullOrWhiteSpace(body)) return false;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            // A relay error payload is valid JSON and carries no tokens — treat it as a shape we
            // understand, with nothing in it, so the caller reports "incomplete" not "malformed".
            tokens = new EbayRelayTokens(
                Str(doc.RootElement, "access_token"),
                Str(doc.RootElement, "refresh_token"),
                Int(doc.RootElement, "expires_in", 7200),
                Int(doc.RootElement, "refresh_token_expires_in", 47304000),
                Str(doc.RootElement, "token_type") is { Length: > 0 } t ? t : "User Access Token");
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement el, string name, int fallback) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && i > 0
            ? i : fallback;
}

// ── Refusals from the token endpoint ──────────────────────────────────────────

/// <summary>What a failed token refresh means for the stored refresh token.</summary>
public enum EbayRefreshFailure
{
    /// <summary>eBay's end, the network, or a configuration problem this end. The stored refresh
    /// token is untouched, because nothing about this says it is bad.</summary>
    Transient,
    /// <summary>eBay said <c>invalid_grant</c>: this grant is dead and no retry will revive it.</summary>
    InvalidGrant
}

/// <summary>
/// Decides whether a refresh failure is allowed to destroy the stored refresh token.
/// </summary>
/// <remarks>
/// <para>
/// This is the single most expensive judgement in the sign-in path. The refresh token is the whole
/// connection — it lasts 18 months and cannot be re-issued without the seller personally going
/// through eBay's consent screen again. Throwing it away over a dropped Wi-Fi connection, a 500
/// from eBay, or a Client Secret that was mistyped ten seconds ago turns a wait-and-retry into a
/// re-login, and there is no way back from it.
/// </para>
/// <para>
/// So only eBay explicitly saying <c>invalid_grant</c> counts. Notably a bare HTTP 400 does not —
/// eBay returns 400 for a malformed request too — and neither does 401, which on this endpoint
/// means the Basic auth (Client ID / Secret) was refused, not that the grant was.
/// </para>
/// </remarks>
public static class EbayRefreshClassifier
{
    public static EbayRefreshFailure Classify(int? httpStatus, string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return EbayRefreshFailure.Transient;

        // Only a 4xx can carry a real grant verdict; a 5xx body mentioning invalid_grant is an
        // error page quoting itself, not eBay adjudicating this token.
        if (httpStatus is not (>= 400 and < 500)) return EbayRefreshFailure.Transient;

        return MentionsInvalidGrant(responseBody) ? EbayRefreshFailure.InvalidGrant : EbayRefreshFailure.Transient;
    }

    private static bool MentionsInvalidGrant(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.String)
            {
                return string.Equals(err.GetString(), "invalid_grant", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (JsonException)
        {
            // Not JSON. eBay's gateway sometimes wraps the payload in HTML on the way through, and
            // the marker is still the only thing being looked for.
        }

        return body.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase);
    }
}
