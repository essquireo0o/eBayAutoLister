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
        // Send Offer to Interested Buyers. Sellers connected before this was added keep working
        // everywhere else; the offers screen tells them to reconnect (EbayPermissionException).
        "https://api.ebay.com/oauth/api_scope/sell.negotiation",
    ];

    public static string ScopeParameter => string.Join(" ", Scopes);

    /// <param name="bindingProblem">
    /// <see cref="ServerBinding.Problem"/> — non-null when this app is not on the port the relay
    /// redirects to. Only fatal for the relay flow; a sandbox RuName points wherever its owner set it.
    /// </param>
    public static EbayAuthUrlProblem? Check(
        string? clientId, string? clientSecret, string? ruName, bool sandbox, string? bindingProblem)
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
                "Open Settings → eBay → Advanced and paste the RuName whose accepted URL is https://inglisting.com/api/ebay/callback.");

        // Sandbox never goes through the relay — its RuName points at whatever the developer
        // registered — so the local port is not part of that round trip.
        if (!sandbox && bindingProblem is not null)
            return new("wrong_port", bindingProblem,
                $"Restart ING AutoLister so it is serving on {AppPaths.BaseUrl}, then connect eBay again.");

        return null;
    }

    /// <summary>Builds the consent URL. Callers must have cleared <see cref="Check"/> first.</summary>
    public static string Build(string authBaseUrl, string clientId, string redirectUri, string state) =>
        $"{authBaseUrl}?client_id={Uri.EscapeDataString(clientId)}" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        $"&response_type=code&scope={Uri.EscapeDataString(ScopeParameter)}" +
        $"&state={Uri.EscapeDataString(state)}";
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
