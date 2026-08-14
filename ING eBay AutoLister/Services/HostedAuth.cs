using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ING_eBay_AutoLister.Services;

/// <summary>What the sign-in and sign-up pages post.</summary>
public sealed record SignInRequest(string? Email, string? Password, string? Code);

/// <summary>
/// Who is allowed to use a hosted deployment, and how every endpoint comes to require an answer.
/// </summary>
/// <remarks>
/// <para>
/// The app has 189 endpoints and none of them asks who is calling, because for its whole life it
/// has been one seller's program on their own machine, reachable only at
/// <c>http://localhost:9332</c>. Put that same binary on a public address and every one of those
/// endpoints hands a stranger the owner's eBay account and the owner's Anthropic key. So under
/// HOSTED the default flips: an endpoint is closed unless it is on the allow-list, rather than
/// open unless someone remembered to close it. A new endpoint added next year is protected by
/// having been written, not by anyone noticing it needs to be.
/// </para>
/// <para>
/// That default is a <see cref="AuthorizationOptions.FallbackPolicy"/>, which applies to every
/// endpoint carrying no authorization metadata of its own — all 189 of them. The allow-list is the
/// handful of things that carry <c>AllowAnonymous</c> explicitly, and it is exactly:
/// the sign-in and sign-up endpoints, <see cref="HealthPath"/>, and the static UI assets.
/// </para>
/// <para>
/// The static UI assets are public because they have to be: they are the sign-in page's own HTML,
/// CSS and JavaScript, and a login screen that requires a login to render is a locked door with
/// the key inside. They are also worth nothing on their own — every byte of a seller's data
/// arrives through an API call, and those are all closed. The two static mounts that are NOT UI
/// assets, <c>/photos</c> and <c>/generated-photos</c>, hold the seller's own item photographs, so
/// <see cref="ProtectedStaticMounts"/> puts them behind the sign-in with the API.
/// </para>
/// <para>
/// The desktop build gets none of this — no cookie, no users table, no sign-in page — and every
/// method here is a no-op when <see cref="IsHostedBuild"/> is false. It is one person, on their own
/// machine, on a loopback port that nothing outside the machine can reach; a password on it would
/// protect the seller from themselves and nobody else. Which build is running is decided by the
/// compiler here and passed as a plain <c>bool</c> everywhere else, so the tests can drive both
/// behaviours from one build instead of proving only whichever half they were compiled into.
/// </para>
/// </remarks>
public static class HostedAuth
{
    /// <summary>The sign-in page. Also the cookie handler's login path, so an expired session lands here.</summary>
    public const string SignInPath = "/signin.html";

    /// <summary>The sign-up page.</summary>
    public const string SignUpPath = "/signup.html";

    /// <summary>Liveness for the host's health check. Says nothing but "the process is answering".</summary>
    public const string HealthPath = "/health";

    public const string SignInApi  = "/api/auth/sign-in";
    public const string SignUpApi  = "/api/auth/sign-up";
    public const string SignOutApi = "/api/auth/sign-out";
    public const string MeApi      = "/api/auth/me";

    /// <summary>
    /// Set on the 401 an API call gets when nobody is signed in. The page's JavaScript watches for
    /// it and sends the browser to the sign-in page; without a marker it would have to treat every
    /// 401 from every endpoint — including eBay's, relayed back — as a lost session.
    /// </summary>
    public const string AuthRequiredHeader = "X-Auth-Required";

    /// <summary>
    /// Static mounts holding the seller's own photographs rather than the product's UI. Served by
    /// static-file middleware, which runs before routing and so is untouched by the fallback
    /// policy — these are gated by hand in <see cref="UseSignedInUser"/> instead.
    /// </summary>
    public static readonly string[] ProtectedStaticMounts = ["/photos", "/generated-photos"];

    /// <summary>
    /// Optional configuration key. When set, sign-up also requires this code, so a deployment the
    /// owner is paying the AI bill for is not open to the whole internet. Unset means open sign-up.
    /// </summary>
    public const string SignUpCodeSetting = "Auth:SignUpCode";

    /// <summary>True in the hosted build, false in the desktop build. The one compile-time switch.</summary>
    public static bool IsHostedBuild =>
#if HOSTED
        true;
#else
        false;
#endif

    /// <summary>
    /// Cookie authentication, the closed-by-default authorization policy, and the users table.
    /// Call before <c>builder.Build()</c>. Does nothing in the desktop build.
    /// </summary>
    /// <param name="hosted">Overrides <see cref="IsHostedBuild"/>. Tests pass both.</param>
    /// <param name="secureCookie">
    /// Whether the cookie carries <c>Secure</c>. True by default in a hosted build: it is the
    /// session that owns an eBay account, and it must never cross a plain-HTTP hop. Only a test
    /// talking to a loopback Kestrel over http has any business setting this to false.
    /// </param>
    public static void AddAccounts(WebApplicationBuilder builder, bool? hosted = null, bool? secureCookie = null)
    {
        if (!(hosted ?? IsHostedBuild)) return;

        // Only if the caller has not already supplied one. Lets a test point the table at a
        // scratch file without a content root, and leaves the app's own registration alone.
        builder.Services.TryAddSingleton<UserStore>();

        // Emails the owner when someone signs up. A no-op unless Notify:Smtp is configured, so it is
        // harmless everywhere it is not wanted. See SignupNotifier.
        builder.Services.TryAddSingleton<SignupNotifier>();

        // Without this the key ring is per-process and in memory, so every restart or redeploy
        // invalidates every cookie and signs the whole userbase out. The keys sit beside the
        // database, which is the directory a hosted deployment already has to make persistent.
        var keyRing = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "auth-keys");
        Directory.CreateDirectory(keyRing);
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keyRing))
            .SetApplicationName("ING Listing Engine");

        builder.Services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name        = "ing_session";
                options.Cookie.HttpOnly    = true;
                // Lax, not None. The cookie is what authorizes a publish to eBay, and Lax is what
                // stops another site's form post from carrying it — this app has no CSRF token of
                // its own, and a cross-site POST that the browser refuses to attach the session to
                // is one that arrives unauthenticated and gets the same 401 as any stranger.
                options.Cookie.SameSite     = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = (secureCookie ?? true)
                    ? CookieSecurePolicy.Always
                    : CookieSecurePolicy.SameAsRequest;

                options.ExpireTimeSpan    = TimeSpan.FromDays(14);
                options.SlidingExpiration = true;
                options.LoginPath         = SignInPath;
                options.AccessDeniedPath  = SignInPath;
                options.ReturnUrlParameter = "next";

                // A browser asking for a page gets sent to the sign-in page; an API call gets a
                // 401. Redirecting an API call would answer fetch() with 200 and a login page,
                // which every caller in app.js would try to parse as its own JSON.
                options.Events.OnRedirectToLogin        = context => Refuse(context, StatusCodes.Status401Unauthorized);
                options.Events.OnRedirectToAccessDenied = context => Refuse(context, StatusCodes.Status403Forbidden);
            });

        // TLS is terminated by the reverse proxy, so every request reaches Kestrel over plain HTTP
        // and the app would otherwise believe that is what the user is on. It matters because the
        // app builds absolute URLs: without this, the sign-in challenge answers a browser with
        // "Location: http://host/signin.html?next=…", and the eBay OAuth callback — which must be
        // HTTPS or eBay rejects it — is derived the same way. The proxy states the truth in
        // X-Forwarded-Proto; this is what reads it.
        //
        // KnownIPNetworks and KnownProxies are cleared because the defaults trust only loopback, and
        // the proxy is not on loopback as the CONTAINER sees it: Caddy's connection arrives from
        // the Docker bridge gateway. Clearing them means "trust whoever connected", which is only
        // safe because nothing but the proxy can connect — the container publishes 127.0.0.1:8080
        // and nothing else. That is the same fact the compose file exists to guarantee; if the
        // published port is ever moved to 0.0.0.0, a stranger can claim any scheme they like and
        // this becomes the reason why.
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            // Host deliberately absent: the proxy already passes the original Host header through,
            // and honouring X-Forwarded-Host would add a header-injection lever for nothing.
            options.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

        builder.Services.AddAuthorization(options =>
        {
            // The whole point of the task: every endpoint that does not opt out requires a signed-in
            // user. Endpoints are not listed here one by one — a list is a thing to forget to add to.
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });
    }

    /// <summary>
    /// Reads the cookie into <c>HttpContext.User</c>, and closes the static mounts that hold the
    /// seller's photographs. Call after <c>UseCors</c> and before any <c>UseStaticFiles</c>: the
    /// photo guard has to run before the middleware that would serve the file.
    /// </summary>
    public static void UseSignedInUser(WebApplication app, bool? hosted = null)
    {
        if (!(hosted ?? IsHostedBuild)) return;

        // Before authentication and before the static mounts, because it rewrites the scheme every
        // one of them goes on to read. See the options in AddAccounts for why it trusts the caller.
        app.UseForwardedHeaders();

        app.UseAuthentication();

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (context.User?.Identity?.IsAuthenticated != true
                && ProtectedStaticMounts.Any(mount => path.StartsWithSegments(mount)))
            {
                // Same treatment as a closed endpoint: the browser is sent to the sign-in page,
                // an <img> or fetch is refused. ChallengeAsync routes through the same events.
                await context.ChallengeAsync();
                return;
            }

            await next(context);
        });
    }

    /// <summary>
    /// Applies the closed-by-default policy to every endpoint. Call after the static-file mounts
    /// and before the endpoints are mapped.
    /// </summary>
    public static void RequireSignIn(WebApplication app, bool? hosted = null)
    {
        if (!(hosted ?? IsHostedBuild)) return;
        app.UseAuthorization();
    }

    /// <summary>
    /// Sign up, sign in, sign out, who-am-I and the health check. Nothing is mapped in the desktop
    /// build — it has no user concept at all, and an endpoint that creates accounts on a
    /// single-seller machine is only a way to lock that seller out of their own app.
    /// </summary>
    public static void MapAccountEndpoints(WebApplication app, bool? hosted = null)
    {
        if (!(hosted ?? IsHostedBuild)) return;

        // ── The allow-list, in full ───────────────────────────────────────────────────────────
        // Everything else this app maps — all 189 endpoints — is closed by the fallback policy.

        // GET and HEAD. HEAD because that is what uptime monitors send — and a route that matches
        // the path but not the method does not fall through to "not found": routing selects a
        // 405 endpoint, which carries none of this endpoint's metadata, so the AllowAnonymous below
        // does not cover it and the fallback policy challenges it instead. The visible symptom is a
        // health check that answers a redirect to the sign-in page and a monitor that reports the
        // site down while it is up.
        app.MapMethods(HealthPath, [HttpMethods.Get, HttpMethods.Head], () => Results.Ok(new { status = "ok" }))
           .AllowAnonymous();

        app.MapPost(SignUpApi, async (SignInRequest body, UserStore users, IConfiguration config, SignupNotifier notifier, HttpContext context) =>
        {
            // A deployment where the owner's own AI key pays for every generation cannot have
            // sign-up open to the internet unless the owner decided it should be. Unset = open.
            var required = config[SignUpCodeSetting];
            if (!string.IsNullOrWhiteSpace(required) && !string.Equals(required, body.Code?.Trim(), StringComparison.Ordinal))
                return Results.Json(new { error = "That sign-up code is not right." },
                                    statusCode: StatusCodes.Status403Forbidden);

            var result = users.Create(body.Email, body.Password);
            if (!result.Succeeded)
                return Results.Json(new { error = result.Message },
                                    statusCode: StatusCodes.Status400BadRequest);

            await SignInAsync(context, result.User!);

            // Tell the owner, fire-and-forget: the notification must not delay the response or fail
            // the sign-up (SignupNotifier swallows its own errors). Behind Caddy the socket peer is
            // the proxy, so prefer the forwarded client IP when the proxy set one.
            var ip = context.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim();
            if (string.IsNullOrWhiteSpace(ip)) ip = context.Connection.RemoteIpAddress?.ToString();
            _ = notifier.NotifySignupAsync(result.User!.Email, ip, context.Request.Headers.UserAgent.ToString());

            return Results.Ok(new { email = result.User!.Email });
        }).AllowAnonymous();

        app.MapPost(SignInApi, async (SignInRequest body, UserStore users, HttpContext context) =>
        {
            var user = users.Verify(body.Email, body.Password);
            if (user is null)
                // One message for both "no such account" and "wrong password". Telling them apart
                // turns the sign-in form into a way to ask which addresses have accounts here.
                return Results.Json(new { error = "That email address and password do not match an account." },
                                    statusCode: StatusCodes.Status401Unauthorized);

            await SignInAsync(context, user);
            return Results.Ok(new { email = user.Email });
        }).AllowAnonymous();

        // Not on the allow-list, and that is deliberate: signing out is something only a signed-in
        // session can do, and a request with no session is already in the state it is asking for.
        app.MapPost(SignOutApi, async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { signedOut = true });
        });

        app.MapGet(MeApi, (HttpContext context) => Results.Ok(new
        {
            signedIn = true,
            email    = context.User.FindFirstValue(ClaimTypes.Email) ?? context.User.Identity?.Name,
            userId   = CurrentUserId(context),
        }));
    }

    /// <summary>
    /// The signed-in user's row id, or null in the desktop build where there is nobody to be.
    /// The per-user credential and data work that follows this change keys on it.
    /// </summary>
    public static long? CurrentUserId(HttpContext context)
    {
        var raw = context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        return long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                             System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    /// <summary>
    /// The row id of whoever is making the request in flight, or null when there is no request — a
    /// background service — or nobody signed in on it. How the singletons that must answer
    /// per-request find out who is asking: <see cref="PerUserCredentialsSource"/> for the eBay
    /// account, <see cref="UserScope"/> for the rows.
    /// </summary>
    public static long? CurrentUserId(IHttpContextAccessor accessor)
    {
        var context = accessor.HttpContext;
        if (context?.User?.Identity?.IsAuthenticated != true) return null;

        return CurrentUserId(context);
    }

    private static Task SignInAsync(HttpContext context, UserAccount user)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name,  user.Email),
            new Claim(ClaimTypes.Email, user.Email),
        ], CookieAuthenticationDefaults.AuthenticationScheme);

        return context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }

    /// <summary>
    /// Turns the cookie handler's "go and log in" into the right answer for who asked: a status
    /// code for an API call, a redirect to the sign-in page for a browser following a link.
    /// </summary>
    private static Task Refuse(RedirectContext<CookieAuthenticationOptions> context, int statusCode)
    {
        if (context.Request.Path.StartsWithSegments("/api") && !IsBrowserNavigation(context.Request))
        {
            context.Response.StatusCode = statusCode;
            context.Response.Headers[AuthRequiredHeader] = "sign-in";
            return Task.CompletedTask;
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    }

    /// <summary>
    /// True when the browser is going to this URL itself rather than a script asking for it.
    /// </summary>
    /// <remarks>
    /// Two endpoints under <c>/api</c> are landing pages and not API calls: eBay sends the seller
    /// back to <c>/api/ebay/callback</c> and the relay to <c>/api/ebay/finish</c>, both as top-level
    /// navigations. If the session had expired while they were away, a bare 401 leaves them on a
    /// blank page at the end of an OAuth round trip with no way back; the sign-in page hands them
    /// the way back and returns them to where they were going.
    /// <para>
    /// <c>Sec-Fetch-Mode</c> rather than sniffing <c>Accept</c>: browsers send <c>navigate</c> only
    /// for a real navigation, and <c>fetch()</c> sends <c>cors</c> or <c>same-origin</c>, so no
    /// caller in app.js can be mistaken for one and handed an HTML login page to parse as JSON.
    /// A client old enough not to send the header at all gets the 401.
    /// </para>
    /// </remarks>
    private static bool IsBrowserNavigation(HttpRequest request) =>
        string.Equals(request.Headers["Sec-Fetch-Mode"], "navigate", StringComparison.OrdinalIgnoreCase);
}
