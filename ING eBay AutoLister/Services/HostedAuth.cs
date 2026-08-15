using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// What the sign-in and sign-up pages post. <paramref name="Name"/> and <paramref name="Code"/> are
/// sign-up's alone; the sign-in page sends neither and the sign-in endpoint reads neither.
/// </summary>
public sealed record SignInRequest(string? Email, string? Password, string? Code, string? Name);

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
/// <summary>
/// The one decision <see cref="HostedAuth.AddAccounts"/> takes that <see cref="HostedAuth.UseSignedInUser"/>
/// has to take the same way, carried between them rather than passed twice.
/// </summary>
/// <param name="SecureCookie">
/// Whether cookies this app sets carry <c>Secure</c>. Both the session cookie and the antiforgery
/// cookie must agree: a test server on loopback http silently drops a Secure cookie, so a
/// disagreement here does not fail loudly, it fails as every POST returning 403 for a reason
/// nothing in the error mentions.
/// </param>
public sealed record HostedAuthOptions(bool SecureCookie);

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

    /// <summary>
    /// The rate-limiting policy carried by the sign-in and sign-up endpoints, and nothing else.
    /// </summary>
    /// <remarks>
    /// <see cref="SignInThrottle"/> counts failures against one address, which stops a password
    /// being guessed. It does nothing about the other shape of the same attack: one host trying two
    /// passwords against ten thousand addresses, where no single address ever reaches five. This is
    /// what makes that expensive — the budget is spent per calling address, on the endpoints where
    /// spending it means guessing, so the spray runs out after <see cref="AuthRequestsPerWindow"/>
    /// tries no matter how many accounts it spreads them over.
    /// </remarks>
    public const string AuthRateLimitPolicy = "auth";

    /// <summary>
    /// Attempts one IP address may make against the auth endpoints per <see cref="AuthWindow"/>.
    /// Set for the person who mistyped, forgot which address they used, and reset the form twice —
    /// not for the script, which exhausts it in under a second.
    /// </summary>
    public const int AuthRequestsPerWindow = 20;

    /// <summary>The window <see cref="AuthRequestsPerWindow"/> is counted over.</summary>
    public static readonly TimeSpan AuthWindow = TimeSpan.FromMinutes(5);

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
    /// <param name="authRequestsPerWindow">
    /// Overrides <see cref="AuthRequestsPerWindow"/>. Only a test proving the limiter fires has any
    /// business setting it — the production value is the constant.
    /// </param>
    public static void AddAccounts(WebApplicationBuilder builder, bool? hosted = null, bool? secureCookie = null,
                                   int? authRequestsPerWindow = null)
    {
        if (!(hosted ?? IsHostedBuild)) return;

        // Recorded rather than re-derived, so UseSignedInUser gives the antiforgery cookie the same
        // Secure flag the session cookie gets without every caller having to say it twice.
        builder.Services.AddSingleton(new HostedAuthOptions(secureCookie ?? true));

        // Only if the caller has not already supplied one. Lets a test point the table at a
        // scratch file without a content root, and leaves the app's own registration alone.
        builder.Services.TryAddSingleton<UserStore>();

        // The per-account half of the answer to brute force. See SignInThrottle for why the count
        // is in the database and keyed on addresses that do not exist as well as ones that do.
        builder.Services.TryAddSingleton<SignInThrottle>();

        // The list of sessions the server will honour, which is what makes signing out a server-side
        // act and a planted session id worthless. See SessionStore.
        builder.Services.TryAddSingleton<SessionStore>();

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
                // Lax, not None. It withholds the cookie from a cross-site form post, which is the
                // usual shape of the attack. It is not the whole defence and is no longer relied on
                // as one — Lax says nothing about a sibling subdomain or a state-changing GET, so
                // the double-submit token in Csrf runs in front of every unsafe request as well.
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

                // Every authenticated request asks the server whether the session in the ticket is
                // one it still honours. Without this the cookie is self-authorising for its whole
                // fourteen days: it decrypts, therefore it is true, and signing out can do nothing
                // but ask the browser nicely to forget. See SessionStore.
                options.Events.OnValidatePrincipal = ValidateSession;
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

        // Per-IP budget on the auth endpoints. The framework's own limiter rather than a
        // hand-rolled dictionary: this one already knows about windows, concurrency, leases and
        // how to say how long to wait, and none of that is worth writing again badly.
        var permits = authRequestsPerWindow ?? AuthRequestsPerWindow;
        builder.Services.AddRateLimiter(options =>
        {
            // 429, not the framework's default 503. A 503 says the server is unwell and invites a
            // retry loop; 429 says the caller asked too often, which is the truth.
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.OnRejected = async (context, cancellationToken) =>
            {
                // The limiter knows when the window rolls over. Passing that on is what lets a
                // client — and the sign-in page — wait the right amount rather than hammer.
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

                // Shaped like every other refusal from these endpoints, because the sign-in page
                // reads body.error and would otherwise show its own "could not sign in" for what is
                // actually "you have been rate limited".
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { error = "Too many sign-in attempts from this connection. Wait a few minutes and try again." },
                    cancellationToken);
            };

            options.AddPolicy(AuthRateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                ClientAddress(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permits,
                    Window      = AuthWindow,
                    // Nothing queues. A sign-in held open until a budget frees up is a browser
                    // spinner for a minute; refusing it immediately is the honest answer.
                    QueueLimit  = 0,
                }));
        });
    }

    /// <summary>
    /// Puts the auth endpoints' per-IP budget on some other endpoint that guards a secret. For the
    /// owner dashboard, which is a password prompt wearing a query string: without a budget it can
    /// be tried as fast as the network allows, and the sign-in form next to it cannot.
    /// </summary>
    /// <remarks>
    /// Conditional because <see cref="AuthRateLimitPolicy"/> is registered in
    /// <see cref="AddAccounts"/>, which does nothing in the desktop build — asking for a policy that
    /// was never added throws at the first request. The desktop build has no need for it either: the
    /// dashboard is on a loopback port on the owner's own machine.
    /// </remarks>
    public static RouteHandlerBuilder RateLimitLikeAuth(this RouteHandlerBuilder builder, bool? hosted = null)
    {
        if (hosted ?? IsHostedBuild) builder.RequireRateLimiting(AuthRateLimitPolicy);
        return builder;
    }

    /// <summary>
    /// Who to charge an auth attempt to. The socket peer, which behind the reverse proxy is the
    /// real client only because <c>UseForwardedHeaders</c> has already rewritten it — which is why
    /// <see cref="UseSignedInUser"/> adds the limiter after that call and not before. Reading
    /// <c>X-Forwarded-For</c> here by hand would let anyone hand themselves a fresh budget per
    /// request by making the header up.
    /// </summary>
    private static string ClientAddress(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// Reads the cookie into <c>HttpContext.User</c>, and closes the static mounts that hold the
    /// seller's photographs. Call after <c>UseCors</c> and before any <c>UseStaticFiles</c>: the
    /// photo guard has to run before the middleware that would serve the file.
    /// </summary>
    public static void UseSignedInUser(WebApplication app, bool? hosted = null, bool? secureCookie = null)
    {
        if (!(hosted ?? IsHostedBuild)) return;

        // Before authentication and before the static mounts, because it rewrites the scheme every
        // one of them goes on to read. See the options in AddAccounts for why it trusts the caller.
        app.UseForwardedHeaders();

        // Straight after the forwarded headers, because the origin check compares against
        // Request.Scheme and that is http until the line above has run. Before authentication and
        // before routing, so an unsafe request without a token is turned away without the cookie
        // being decrypted or an endpoint being selected — a refusal that costs nothing.
        Csrf.UseCsrf(app, hosted,
            secureCookie ?? app.Services.GetService<HostedAuthOptions>()?.SecureCookie);

        // After the forwarded headers, because the limiter partitions on the client address and
        // before that call every request appears to come from the proxy — one bucket for the whole
        // internet, which would rate-limit the userbase as if it were one attacker. Endpoint
        // routing has already run by here (WebApplication inserts it ahead of this middleware), so
        // the policy attached with RequireRateLimiting is visible.
        app.UseRateLimiter();

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

            var result = users.Create(body.Email, body.Password, body.Name);

            // Everything the person got wrong about their own submission is told back to them
            // plainly — the name they left blank, the password that is too short or too common.
            // None of those answers depend on whether the address is registered, so none of them
            // says anything about somebody else's account.
            if (!result.Succeeded && result.Outcome != SignUpOutcome.EmailAlreadyRegistered)
                return Results.Json(new { error = result.Message },
                                    statusCode: StatusCodes.Status400BadRequest);

            if (result.Succeeded)
            {
                // Tell the owner, fire-and-forget: the notification must not delay the response or
                // fail the sign-up (SignupNotifier swallows its own errors). Behind Caddy the socket
                // peer is the proxy, so prefer the forwarded client IP when the proxy set one.
                //
                // Four things go out and no more — name, address, time, IP. Nothing that identifies
                // the session and nothing that could be used to sign in as them: the password is not
                // in scope here, and result.User carries no token. See SignupNotifier.
                var ip = context.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim();
                if (string.IsNullOrWhiteSpace(ip)) ip = context.Connection.RemoteIpAddress?.ToString();
                _ = notifier.NotifySignupAsync(result.User!.Email, ip, result.User!.Name);
            }

            // The same answer whether an account was just created or the address already had one.
            //
            // This is the only response that can be sent without saying which happened, and saying
            // which happened turns the sign-up form into the same address-checking oracle that
            // SignInThrottle and PasswordHash.VerifyNothing go to such lengths to deny the sign-in
            // form. Both branches have already spent one PBKDF2 hash by the time they arrive here —
            // Create hashes before it attempts the insert — so they cost the same to the millisecond
            // as well as reading the same.
            //
            // It is also why signing up no longer signs you in: a session cookie on one branch and
            // not the other is the difference restated in the headers, where a script would read it
            // even more easily than in the body. Whoever really did just create the account has the
            // password they typed a second ago, and the page sends them straight to the sign-in
            // form; whoever was probing gets a sign-in form they cannot get through.
            return Results.Ok(new { ok = true, next = SignInPath });
        }).AllowAnonymous().RequireRateLimiting(AuthRateLimitPolicy);

        app.MapPost(SignInApi, async (SignInRequest body, UserStore users, SignInThrottle throttle, HttpContext context) =>
        {
            // Asked before the password is looked at. A locked address costs one indexed read
            // rather than a PBKDF2 verification, so the lock is also what stops the guessing being
            // a way to spend the server's CPU.
            var standing = throttle.Check(body.Email);
            if (standing.Locked) return Locked(context, standing, throttle.Now);

            var user = users.Verify(body.Email, body.Password);
            if (user is null)
            {
                // Counted whether or not the address is registered — see SignInThrottle. The
                // failure that reaches the limit still answers 401 here, because it genuinely was
                // a wrong password; it is the attempt after it that meets the lock.
                throttle.RecordFailure(body.Email);

                // One message for both "no such account" and "wrong password". Telling them apart
                // turns the sign-in form into a way to ask which addresses have accounts here.
                return Results.Json(new { error = "That email address and password do not match an account." },
                                    statusCode: StatusCodes.Status401Unauthorized);
            }

            // What makes the count consecutive rather than lifetime: four typos and then the right
            // password leaves the seller with a clean slate, not one mistake from being locked out.
            throttle.RecordSuccess(body.Email);

            await SignInAsync(context, user);
            return Results.Ok(new { email = user.Email, name = user.Name });
        }).AllowAnonymous().RequireRateLimiting(AuthRateLimitPolicy);

        // Not on the allow-list, and that is deliberate: signing out is something only a signed-in
        // session can do, and a request with no session is already in the state it is asking for.
        app.MapPost(SignOutApi, async (HttpContext context, SessionStore sessions) =>
        {
            // The server side of signing out, and the half that used to be missing. Clearing the
            // cookie only tells this browser to forget; revoking the row means every copy of that
            // ticket — including one lifted off a shared machine before the button was pressed —
            // stops being honoured on the next request it is used for. See SessionStore.
            sessions.Revoke(context.User.FindFirstValue(SessionStore.SessionIdClaim));

            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { signedOut = true });
        });

        // Where a page that has no antiforgery token yet goes to get one. The token is put on the
        // response by the Csrf middleware, which runs in front of this and every other endpoint;
        // all this has to do is be a safe, anonymous request for that middleware to answer.
        // Anonymous because the sign-in page needs a token before there is anybody to be.
        app.MapGet(Csrf.TokenPath, (HttpContext context) =>
            Results.Ok(new { token = Csrf.TokenFor(context) ?? string.Empty }))
           .AllowAnonymous();

        app.MapGet(MeApi, (HttpContext context, UserStore users) =>
        {
            var id      = CurrentUserId(context);
            var email   = context.User.FindFirstValue(ClaimTypes.Email) ?? context.User.Identity?.Name;

            // The row rather than the claim, so a name is right even in a session whose cookie was
            // issued before names existed — the cookie lasts fourteen days and is renewed sliding,
            // so "it will sort itself out on the next sign-in" can mean months. The claim below is
            // the fallback for the moment between the row being deleted and the cookie expiring.
            var account = id is null ? null : users.Find(id.Value);

            return Results.Ok(new
            {
                signedIn = true,
                email,
                userId   = id,
                name     = account?.Name ?? context.User.FindFirstValue(ClaimTypes.GivenName) ?? string.Empty,
                // What to actually put on the screen: the name when there is one, the address when
                // there is not. Decided here so every caller shows the same thing.
                displayName = account?.DisplayName ?? email,
            });
        });
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

    /// <summary>
    /// The answer to an attempt on a locked account: 429, the same shape of error body every other
    /// refusal from this endpoint uses, and <c>Retry-After</c> so a client need not guess.
    /// </summary>
    /// <remarks>
    /// It says plainly that the account is locked and roughly when it lifts. That is not a leak:
    /// somebody guessing passwords already knows they have been guessing, and the only person the
    /// vaguer message would fool is the seller who mistyped and is now staring at "email and
    /// password do not match" while typing the right password. The one thing withheld is whether
    /// the address is registered — this same answer comes back for an address that never was.
    /// </remarks>
    private static IResult Locked(HttpContext context, SignInLockout lockout, DateTimeOffset now)
    {
        var remaining = lockout.Remaining(now);
        context.Response.Headers.RetryAfter =
            ((int)Math.Ceiling(remaining.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

        return Results.Json(new
        {
            error       = SignInThrottle.LockedMessage(remaining),
            locked      = true,
            lockedUntil = lockout.Until,
        }, statusCode: StatusCodes.Status429TooManyRequests);
    }

    /// <summary>
    /// Puts a freshly issued session in the cookie. The only path to an authenticated ticket, and
    /// the only caller of <see cref="SessionStore.Issue"/>.
    /// </summary>
    /// <remarks>
    /// Two things happen here that make session fixation a non-event. The identifier is minted after
    /// the password was checked and is never taken from the request, so a value an attacker planted
    /// in the browser beforehand cannot be the one the authenticated session ends up known by. And
    /// whatever session the caller arrived holding is revoked first — signing in while already
    /// signed in retires the old ticket rather than leaving two live ones for the same account,
    /// which is what an attacker who got their own session into the seller's browser would be
    /// counting on.
    /// </remarks>
    private static async Task SignInAsync(HttpContext context, UserAccount user)
    {
        var sessions = context.RequestServices.GetRequiredService<SessionStore>();

        // Before the new one is issued, so a sign-in that fails halfway does not leave the old
        // session live alongside a new one.
        sessions.Revoke(context.User?.FindFirstValue(SessionStore.SessionIdClaim));

        var sessionId = sessions.Issue(user.Id);

        // Name stays the email address: it is the identity's unique handle, and things that log or
        // key on User.Identity.Name need it to be the one that is unique. What the person is called
        // goes in GivenName, which is a label and is not required to be unique to anybody.
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name,  user.Email),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.GivenName, user.Name),
            new Claim(SessionStore.SessionIdClaim, sessionId),
        ], CookieAuthenticationDefaults.AuthenticationScheme);

        await context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }

    /// <summary>
    /// Asks <see cref="SessionStore"/> whether the session named in a decrypted ticket is one the
    /// server still honours, and throws the principal away when it is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A ticket that fails here is not merely ignored for the one request:
    /// <c>RejectPrincipal</c> plus <c>SignOutAsync</c> also clears the cookie, so a browser holding
    /// a revoked session stops sending it rather than being refused on every request forever.
    /// </para>
    /// <para>
    /// Tickets issued before this existed carry no <c>sid</c> claim, and are rejected — everyone
    /// signed in at the moment this deploys signs in again. That is the correct way round: the
    /// alternative is to honour a claimless ticket, which is exactly the ticket an attacker would
    /// forge if they could, and it would keep the old unrevocable sessions alive for two more weeks.
    /// </para>
    /// </remarks>
    private static async Task ValidateSession(CookieValidatePrincipalContext context)
    {
        // Both read off context.Principal and not off HttpContext.User. This event is what decides
        // whether the ticket becomes the user, so at this point HttpContext.User is still the empty
        // principal — reading it here rejects every session on every request, and does it quietly.
        var raw       = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var sessionId = context.Principal?.FindFirstValue(SessionStore.SessionIdClaim);
        var sessions  = context.HttpContext.RequestServices.GetRequiredService<SessionStore>();

        var userId = long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id
            : (long?)null;

        if (userId is not null && sessions.IsLive(sessionId, userId.Value))
        {
            sessions.Touch(sessionId);
            return;
        }

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
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
