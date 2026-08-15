using System.Security.Cryptography;
using System.Text;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Stops another website making this one act on the seller's behalf, by requiring every
/// state-changing request to prove it was made by a page served from this origin.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HostedAuth"/> sets the session cookie <c>SameSite=Lax</c>, and the comment there says
/// that is what stops a cross-site form post carrying the session. That is true of the shape the
/// attack usually takes and it is not the whole story, which is why this file exists.
/// </para>
/// <para>
/// Lax withholds the cookie from a cross-site POST. It does <em>not</em> withhold it from a
/// cross-site top-level GET navigation, so any endpoint that changes something on a GET is
/// unprotected by it; it does not help against a page on a sibling host, because SameSite is judged
/// on the registrable domain and <c>anything.inglisting.com</c> is same-site with
/// <c>app.inglisting.com</c> however little the app trusts it; and it is a browser behaviour, so it
/// is worth exactly what the browser in front of it is worth. Lax is a good default and a bad only
/// line of defence.
/// </para>
/// <para>
/// What is required instead is a secret the calling page must be able to read, which a cross-origin
/// page cannot: the double-submit token. A random value is put in a cookie the page's own JavaScript
/// can read, and every unsafe request has to echo it back in <see cref="HeaderName"/>. An attacker's
/// page can make the browser send the cookie — that is the whole problem — but the same-origin
/// policy stops it ever <em>reading</em> the cookie, so it cannot produce the header. The cookie is
/// therefore deliberately not <c>HttpOnly</c>, which is the one thing about this design that looks
/// wrong and is not: the token is not a credential, it authorises nothing on its own, and a value
/// the page cannot read is a value the page cannot echo.
/// </para>
/// <para>
/// The <c>Origin</c> check in front of it is the cheap half, and catches the case where the token
/// leaks. Browsers attach <c>Origin</c> to every unsafe request, including form posts, and its value
/// is set by the browser rather than the page — so a request that names a different origin is
/// refused before its token is looked at, and a request that names this one had to come from a page
/// here. Neither half is trusted alone: an old client that sends no <c>Origin</c> still needs the
/// token, and a request with the right <c>Origin</c> still needs it too.
/// </para>
/// <para>
/// It applies to every unsafe method on every path, with no allow-list. There is nothing to put on
/// one — this deployment has no webhook and no machine caller; all 92 <c>MapPost</c> and 5
/// <c>MapDelete</c> endpoints are the app's own pages talking to their own server. If a webhook is
/// ever added it will have to be exempted here explicitly and carry its own signature check, and
/// having to come and do that on purpose is the point.
/// </para>
/// </remarks>
public static class Csrf
{
    /// <summary>The cookie holding the token. Readable by this origin's JavaScript, by design.</summary>
    public const string CookieName = "ing_csrf";

    /// <summary>The header the token must be echoed in. A header a cross-origin form cannot set.</summary>
    public const string HeaderName = "X-CSRF-Token";

    /// <summary>
    /// Set on the 403 a missing or stale token earns, so the page can tell it apart from a
    /// permission problem and fetch a fresh token instead of showing the seller an error.
    /// </summary>
    public const string RequiredHeader = "X-CSRF-Required";

    /// <summary>Where a page with no token yet can go and get one. Safe, anonymous, and cheap.</summary>
    public const string TokenPath = "/api/auth/csrf";

    /// <summary>
    /// Where the middleware leaves the token for the rest of the pipeline. Needed because on the
    /// request that mints one the value exists only on the response — <c>Request.Cookies</c> still
    /// reflects what the browser sent, which was nothing — and <see cref="TokenPath"/> has to be
    /// able to answer with it on that very first visit.
    /// </summary>
    private const string ItemsKey = "ing.csrf.token";

    /// <summary>The token for this request, whether it arrived on the cookie or was just issued.</summary>
    public static string? TokenFor(HttpContext context) =>
        context.Items.TryGetValue(ItemsKey, out var token) ? token as string : context.Request.Cookies[CookieName];

    /// <summary>Matches the session cookie's fourteen days, so the two do not expire out of step.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    /// <summary>Methods that are not supposed to change anything, and so are not checked.</summary>
    private static readonly HashSet<string> SafeMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS", "TRACE" };

    /// <summary>
    /// Issues the token to anyone who has not got one, and refuses any unsafe request that cannot
    /// produce it. Call after <c>UseForwardedHeaders</c> — the origin check compares against
    /// <c>Request.Scheme</c>, which is <c>http</c> until that call has run and would then reject
    /// every request the proxy forwards. A no-op in the desktop build.
    /// </summary>
    public static void UseCsrf(WebApplication app, bool? hosted = null, bool? secureCookie = null)
    {
        if (!(hosted ?? HostedAuth.IsHostedBuild)) return;

        app.Use(async (context, next) =>
        {
            var token = context.Request.Cookies[CookieName];

            // Anyone who arrives without one gets one, on the way in rather than the way out: the
            // response may be a file streamed by static-file middleware, and by the time that has
            // started there is no adding a header to it. A page load is what primes the token, so
            // by the time the seller can click anything their browser already has it.
            if (string.IsNullOrEmpty(token) && SafeMethods.Contains(context.Request.Method))
            {
                token = Issue();
                context.Response.Cookies.Append(CookieName, token, new CookieOptions
                {
                    // Readable by this origin's scripts. See the class remarks for why that is safe.
                    HttpOnly = false,
                    SameSite = SameSiteMode.Lax,
                    Secure   = secureCookie ?? true,
                    Path     = "/",
                    Expires  = DateTimeOffset.UtcNow.Add(Lifetime),
                });
            }

            context.Items[ItemsKey] = token;

            if (!SafeMethods.Contains(context.Request.Method) && !IsPermitted(context, token))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.Headers[RequiredHeader] = "token";
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "This request could not be verified as coming from the app. Reload the page and try again.",
                });
                return;
            }

            await next(context);
        });
    }

    /// <summary>
    /// The two checks, both of which an unsafe request has to pass. Public so a test can drive it
    /// without standing up a server.
    /// </summary>
    public static bool IsPermitted(HttpContext context, string? cookieToken)
    {
        if (!OriginIsOurs(context.Request)) return false;

        var sent = context.Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrEmpty(sent) || string.IsNullOrEmpty(cookieToken)) return false;

        return FixedTimeEquals(sent, cookieToken);
    }

    /// <summary>
    /// Whether the browser says this request came from a page on this site. True when there is no
    /// <c>Origin</c> at all — that is not a pass, it is a deferral: the token check runs next and a
    /// caller old enough to omit the header still has to produce one.
    /// </summary>
    /// <remarks>
    /// <c>Referer</c> is deliberately not consulted as a fallback. It is stripped by privacy tools
    /// and by <c>Referrer-Policy</c>, so treating its absence as suspicious would refuse real
    /// sellers, and treating its presence as proof would be trusting a header that is easier to
    /// influence than <c>Origin</c>.
    /// </remarks>
    private static bool OriginIsOurs(HttpRequest request)
    {
        var origin = request.Headers.Origin.FirstOrDefault();
        if (string.IsNullOrEmpty(origin)) return true;

        // "null" is what a sandboxed iframe or a file:// page sends. It is not this origin.
        if (string.Equals(origin, "null", StringComparison.OrdinalIgnoreCase)) return false;

        // Compared as a parsed URL rather than as text, so that a host which merely starts with
        // ours — app.inglisting.com.evil.test — is not a prefix match away from being accepted.
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var sent)) return false;

        var host = request.Host;
        if (!host.HasValue) return false;

        return string.Equals(sent.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(sent.Host, host.Host, StringComparison.OrdinalIgnoreCase)
            && sent.Port == (host.Port ?? DefaultPort(request.Scheme));
    }

    private static int DefaultPort(string scheme) =>
        string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80;

    /// <summary>A fresh token. 256 bits, base64url, from the CSPRNG.</summary>
    public static string Issue() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
               .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Compares the sent token against the stored one without leaking, in how long it took, how
    /// much of it was right. Same reasoning as <see cref="PasswordHash"/>: this is a secret being
    /// compared against attacker-supplied input, and <c>==</c> on strings returns early.
    /// </summary>
    public static bool FixedTimeEquals(string? a, string? b)
    {
        if (a is null || b is null) return false;

        var left  = Encoding.UTF8.GetBytes(a);
        var right = Encoding.UTF8.GetBytes(b);

        // FixedTimeEquals is only fixed-time for equal lengths; it short-circuits otherwise, which
        // leaks the length. The tokens here are all the same length, so a length mismatch already
        // means "not ours" and there is nothing further to hide.
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
