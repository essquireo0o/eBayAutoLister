using System.Net;
using System.Net.Http.Json;
using ING_eBay_AutoLister.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// Proves the one thing that decides whether this app can be put on the internet: under HOSTED an
/// ordinary API endpoint refuses a caller who has not signed in, and in the desktop build the same
/// endpoint answers exactly as it always has.
/// </summary>
/// <remarks>
/// <para>
/// These start a real Kestrel server on a loopback port and talk to it over HTTP with a real
/// cookie jar. Nothing else would do. The protection is a fallback authorization policy, a cookie
/// handler and the order of four middleware calls — a unit test that asserted the policy object
/// had been constructed would pass just as happily with the pipeline wired up wrongly, which is
/// the failure mode that matters here.
/// </para>
/// <para>
/// The server built below is not the app: it is the app's own <see cref="HostedAuth"/> calls, in
/// the app's order, around a stand-in endpoint. <see cref="HostedAuthWiringTests"/> is what holds
/// Program.cs to making those same calls in that same order.
/// </para>
/// <para>
/// Both builds are exercised from one compilation because <see cref="HostedAuth"/> takes hosted as
/// an argument. Tests compiled with <c>#if HOSTED</c> could only ever prove half of this, and it
/// would be the half nobody runs.
/// </para>
/// </remarks>
public class HostedAuthTests
{
    /// <summary>Stands in for all 189. Nothing about it opts out of the policy — that is the point.</summary>
    private const string RepresentativeEndpoint = "/api/setup/status";

    private const string Email    = "seller@example.com";
    private const string Password = "a-long-enough-password";

    // ── The acceptance: closed when hosted, open on the desktop ──────────────────────────────

    [Fact]
    public async Task Hosted_refuses_an_unauthenticated_api_call()
    {
        await using var server = await StartAsync(hosted: true);

        var response = await server.Client.GetAsync(RepresentativeEndpoint);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // The marker app.js watches for. Without it the page cannot tell a lost session from a
        // 401 relayed back from eBay, and would bounce the seller to sign-in for both.
        Assert.Equal("sign-in", response.Headers.GetValues(HostedAuth.AuthRequiredHeader).Single());
    }

    [Fact]
    public async Task Desktop_answers_the_same_endpoint_with_no_sign_in_at_all()
    {
        await using var server = await StartAsync(hosted: false);

        var response = await server.Client.GetAsync(RepresentativeEndpoint);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains(HostedAuth.AuthRequiredHeader));
    }

    [Fact]
    public async Task Desktop_has_no_sign_in_to_offer()
    {
        await using var server = await StartAsync(hosted: false);

        // Not "mapped but always succeeds" — not mapped. The desktop build has no user concept,
        // and an endpoint that creates accounts on a single-seller machine is only a way for
        // something else on that machine to lock the seller out of their own app.
        var signIn = await server.Client.PostAsJsonAsync(HostedAuth.SignInApi, new { email = Email, password = Password });
        var health = await server.Client.GetAsync(HostedAuth.HealthPath);

        Assert.Equal(HttpStatusCode.NotFound, signIn.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, health.StatusCode);
    }

    // ── Signing up and signing in ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Signing_up_opens_the_endpoint_that_was_closed()
    {
        await using var server = await StartAsync(hosted: true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Client.GetAsync(RepresentativeEndpoint)).StatusCode);

        var signUp = await server.SignUpAsync(Email, Password);
        Assert.Equal(HttpStatusCode.OK, signUp.StatusCode);

        // Signing up signs you in — there is no second step and no confirmation screen to lose
        // people on.
        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync(RepresentativeEndpoint)).StatusCode);
    }

    [Fact]
    public async Task Signing_in_after_signing_out_opens_it_again()
    {
        await using var server = await StartAsync(hosted: true);
        await server.SignUpAsync(Email, Password);

        Assert.Equal(HttpStatusCode.OK, (await server.Client.PostAsync(HostedAuth.SignOutApi, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Client.GetAsync(RepresentativeEndpoint)).StatusCode);

        var signIn = await server.Client.PostAsJsonAsync(HostedAuth.SignInApi, new { email = Email, password = Password });

        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync(RepresentativeEndpoint)).StatusCode);
    }

    [Fact]
    public async Task The_wrong_password_leaves_the_endpoint_closed()
    {
        await using var server = await StartAsync(hosted: true);
        await server.SignUpAsync(Email, Password);
        await server.Client.PostAsync(HostedAuth.SignOutApi, null);

        var signIn = await server.Client.PostAsJsonAsync(HostedAuth.SignInApi,
            new { email = Email, password = Password + "!" });

        Assert.Equal(HttpStatusCode.Unauthorized, signIn.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Client.GetAsync(RepresentativeEndpoint)).StatusCode);
    }

    [Fact]
    public async Task An_unregistered_address_is_refused_in_the_same_words_as_a_wrong_password()
    {
        await using var server = await StartAsync(hosted: true);
        await server.SignUpAsync(Email, Password);
        await server.Client.PostAsync(HostedAuth.SignOutApi, null);

        var wrongPassword = await server.Client.PostAsJsonAsync(HostedAuth.SignInApi,
            new { email = Email, password = "not-the-password" });
        var noSuchAccount = await server.Client.PostAsJsonAsync(HostedAuth.SignInApi,
            new { email = "nobody@example.com", password = Password });

        // Different answers here would turn the sign-in form into a way to ask which addresses
        // have accounts on this deployment.
        Assert.Equal(HttpStatusCode.Unauthorized, noSuchAccount.StatusCode);
        Assert.Equal(await wrongPassword.Content.ReadAsStringAsync(),
                     await noSuchAccount.Content.ReadAsStringAsync());
    }

    // ── Brute force ──────────────────────────────────────────────────────────────────────────
    //
    // Measured on the live site before any of this existed: eight wrong passwords in a row for one
    // account answered 401 eight times at full speed, with nothing between the attacker and the
    // ninth. Everything under this heading is the wall that went up.

    /// <summary>The acceptance, in one test: the sixth guess is refused and a neighbour is not.</summary>
    [Fact]
    public async Task The_sixth_wrong_password_is_refused_while_another_account_signs_in_normally()
    {
        const string Neighbour = "someone-else@example.com";

        await using var server = await StartAsync(hosted: true);
        await server.SignUpAsync(Email, Password);
        await server.Client.PostAsync(HostedAuth.SignOutApi, null);
        await server.SignUpAsync(Neighbour, Password);
        await server.Client.PostAsync(HostedAuth.SignOutApi, null);

        // Five genuine misses. Each is answered 401, because each genuinely was a wrong password.
        for (var attempt = 1; attempt <= SignInThrottle.MaxFailures; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Attempt(server, Email, "guess-" + attempt)).StatusCode);

        // The sixth is not answered at all. No hash is computed and no password is compared.
        var sixth = await Attempt(server, Email, "guess-6");
        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);

        // And the right password, during the lock, is refused with it — a lock a correct guess
        // opens is not a lock, it is a delay on the guess that would have worked anyway.
        var correctButLocked = await Attempt(server, Email, Password);
        Assert.Equal(HttpStatusCode.TooManyRequests, correctButLocked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Client.GetAsync(RepresentativeEndpoint)).StatusCode);

        // The lockout is per account. One seller under attack must not take the userbase down
        // with them.
        var neighbour = await Attempt(server, Neighbour, Password);
        Assert.Equal(HttpStatusCode.OK, neighbour.StatusCode);
        await server.Client.PostAsync(HostedAuth.SignOutApi, null);

        // And it lifts on its own. Nobody has to be emailed, and no owner has to go and clear a row.
        server.Wait(SignInThrottle.DefaultLockout + TimeSpan.FromSeconds(1));
        var afterTheWait = await Attempt(server, Email, Password);
        Assert.Equal(HttpStatusCode.OK, afterTheWait.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync(RepresentativeEndpoint)).StatusCode);
    }

    [Fact]
    public async Task The_locked_answer_says_it_is_locked_and_when_it_lifts()
    {
        await using var server = await StartAsync(hosted: true);
        await server.SignUpAsync(Email, Password);
        await server.Client.PostAsync(HostedAuth.SignOutApi, null);

        for (var attempt = 0; attempt <= SignInThrottle.MaxFailures; attempt++)
            await Attempt(server, Email, "wrong");

        var locked = await Attempt(server, Email, "wrong");
        var body = await locked.Content.ReadAsStringAsync();

        // The person on the other end of this is nearly always someone who mistyped their own
        // password. Telling them "email and password do not match" while they type the right one
        // would be hiding a fact from the only party it can help — the attacker already knows.
        Assert.Contains("locked", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("15 minutes", body, StringComparison.Ordinal);
        // Retry-After so a client need not guess, and lockedUntil so the page could show a time.
        Assert.True(locked.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.Contains("lockedUntil", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_good_password_before_the_fifth_miss_clears_the_count()
    {
        await using var server = await StartAsync(hosted: true);
        await server.SignUpAsync(Email, Password);
        await server.Client.PostAsync(HostedAuth.SignOutApi, null);

        // Four typos, then the right password. The seller must not be left one mistake away from
        // a lockout for the rest of the day.
        for (var attempt = 0; attempt < SignInThrottle.MaxFailures - 1; attempt++)
            await Attempt(server, Email, "typo");
        Assert.Equal(HttpStatusCode.OK, (await Attempt(server, Email, Password)).StatusCode);
        await server.Client.PostAsync(HostedAuth.SignOutApi, null);

        for (var attempt = 0; attempt < SignInThrottle.MaxFailures - 1; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Attempt(server, Email, "typo")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Attempt(server, Email, Password)).StatusCode);
    }

    [Fact]
    public async Task An_address_that_never_signed_up_locks_out_in_the_same_words()
    {
        await using var server = await StartAsync(hosted: true);
        await server.SignUpAsync(Email, Password);
        await server.Client.PostAsync(HostedAuth.SignOutApi, null);

        for (var attempt = 0; attempt <= SignInThrottle.MaxFailures; attempt++)
        {
            await Attempt(server, Email, "wrong");
            await Attempt(server, "nobody@example.com", "wrong");
        }

        var registered   = await Attempt(server, Email, "wrong");
        var neverExisted = await Attempt(server, "nobody@example.com", "wrong");

        // If the lock only applied to real accounts, the sixth attempt would answer "locked" for a
        // registered address and "wrong password" for an invented one — and the sign-in form would
        // be a way to ask which addresses have accounts here, which is the thing the identical
        // wording and the decoy hash in PasswordHash.VerifyNothing exist to refuse.
        Assert.Equal(HttpStatusCode.TooManyRequests, registered.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, neverExisted.StatusCode);
        // The sentence, not the whole body: lockedUntil is a timestamp and the two locks were set
        // milliseconds apart, which is a difference about when the guessing started rather than
        // about whether the account exists.
        Assert.Equal(await ErrorOf(registered), await ErrorOf(neverExisted));
    }

    private static async Task<string> ErrorOf(HttpResponseMessage response) =>
        System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync())
              .RootElement.GetProperty("error").GetString()!;

    [Fact]
    public async Task One_host_cannot_spray_a_different_account_every_time()
    {
        // Five guesses per account is no defence at all against a script that never guesses twice
        // at the same account. This is the other half: a budget spent per calling address.
        await using var server = await StartAsync(hosted: true, authRequestsPerWindow: 6);

        for (var account = 0; account < 6; account++)
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await Attempt(server, $"victim-{account}@example.com", "guess")).StatusCode);

        var seventhAccount = await Attempt(server, "victim-6@example.com", "guess");

        // Nothing on that seventh account has failed even once, and it is refused anyway, because
        // the host asking has run out of budget.
        Assert.Equal(HttpStatusCode.TooManyRequests, seventhAccount.StatusCode);
        Assert.Contains("Too many sign-in attempts", await seventhAccount.Content.ReadAsStringAsync(),
                        StringComparison.Ordinal);
        Assert.NotNull(seventhAccount.Headers.RetryAfter);
    }

    [Fact]
    public async Task The_spray_budget_covers_sign_up_as_well_as_sign_in()
    {
        await using var server = await StartAsync(hosted: true, authRequestsPerWindow: 3);

        for (var i = 0; i < 3; i++) await server.SignUpAsync($"someone-{i}@example.com", Password);

        // Creating accounts is the other endpoint that costs the owner something — the AI bill,
        // the database, and an email to him for every one. It shares the budget.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await server.SignUpAsync("flood@example.com", Password)).StatusCode);
    }

    [Fact]
    public async Task An_unregistered_address_takes_about_as_long_to_refuse_as_a_wrong_password()
    {
        await using var server = await StartAsync(hosted: true, authRequestsPerWindow: 200);
        await server.SignUpAsync(Email, Password);
        await server.Client.PostAsync(HostedAuth.SignOutApi, null);

        // The first call through PBKDF2 pays for JIT and for the decoy hash being constructed.
        await Attempt(server, "warmup@example.com", "wrong");

        // Under the lockout threshold on both sides, so every sample here is a real verification
        // and not a cheap refusal.
        var samples = SignInThrottle.MaxFailures - 1;
        var wrongPassword = new List<double>();
        var noSuchAccount = new List<double>();
        for (var i = 0; i < samples; i++)
        {
            wrongPassword.Add(await MillisecondsFor(server, Email, "wrong"));
            noSuchAccount.Add(await MillisecondsFor(server, $"nobody-{i}@example.com", "wrong"));
        }

        var registered = Median(wrongPassword);
        var invented   = Median(noSuchAccount);
        var ratio      = Math.Max(registered, invented) / Math.Min(registered, invented);

        // Not equality — this is a real server on a shared machine and the numbers wobble. What
        // matters is that "no such account" does not return in a fraction of the time, which is
        // what it would do without the decoy hash in PasswordHash.VerifyNothing: a stopwatch and a
        // word list would then be a way to read out the deployment's whole user list.
        Assert.True(ratio < 3, $"wrong password {registered:F0}ms vs unregistered {invented:F0}ms");
    }

    private static Task<HttpResponseMessage> Attempt(TestServer server, string email, string password) =>
        server.Client.PostAsJsonAsync(HostedAuth.SignInApi, new { email, password });

    private static async Task<double> MillisecondsFor(TestServer server, string email, string password)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var response = await Attempt(server, email, password);
        await response.Content.ReadAsStringAsync();
        return started.Elapsed.TotalMilliseconds;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    [Fact]
    public async Task One_address_cannot_sign_up_twice()
    {
        await using var server = await StartAsync(hosted: true);
        await server.SignUpAsync(Email, Password);

        // Same address, different case and padding — the second person would otherwise get their
        // own account and wonder why the first person's listings are not in it.
        var again = await server.SignUpAsync("  Seller@Example.COM ", "a-different-password");

        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task A_password_shorter_than_the_minimum_is_refused()
    {
        await using var server = await StartAsync(hosted: true);

        // Eleven characters, over the wire, past whatever the browser would have checked. The
        // page's minlength is a courtesy; this is the rule.
        var response = await server.SignUpAsync(Email, "elevenchars");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Client.GetAsync(RepresentativeEndpoint)).StatusCode);

        // And it made no account: the same address with a good password still signs up.
        Assert.Equal(HttpStatusCode.OK, (await server.SignUpAsync(Email, "elevencharsX")).StatusCode);
    }

    [Theory]
    [InlineData("passwordpassword")]
    [InlineData("123456789012")]
    [InlineData("seller@example.com")] // the address in the box directly above
    public async Task A_guessable_password_is_refused_however_long_it_is(string password)
    {
        await using var server = await StartAsync(hosted: true);

        var response = await server.SignUpAsync(Email, password);

        Assert.True(password.Length >= PasswordPolicy.MinimumLength, "the point is that length alone is not enough");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Client.GetAsync(RepresentativeEndpoint)).StatusCode);
    }

    // ── The name ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Signing_up_without_a_name_makes_no_account(string? name)
    {
        await using var server = await StartAsync(hosted: true);

        var response = await server.SignUpAsync(Email, Password, name: name);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // Not signed in either — a refused sign-up must not leave a cookie behind.
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Client.GetAsync(RepresentativeEndpoint)).StatusCode);
    }

    [Fact]
    public async Task The_name_given_at_sign_up_is_what_the_app_calls_them()
    {
        await using var server = await StartAsync(hosted: true);
        await server.SignUpAsync(Email, Password, name: "  Ada O'Neill-Vance  ");

        var me = await server.Client.GetFromJsonAsync<Me>(HostedAuth.MeApi);

        Assert.Equal("Ada O'Neill-Vance", me!.Name);
        Assert.Equal("Ada O'Neill-Vance", me.DisplayName);
        Assert.Equal(Email, me.Email);
    }

    [Fact]
    public async Task The_name_survives_signing_out_and_back_in()
    {
        // It is on the row, not only in the cookie that happened to be minted at sign-up.
        await using var server = await StartAsync(hosted: true);
        await server.SignUpAsync(Email, Password, name: "Ada O'Neill-Vance");
        await server.Client.PostAsync(HostedAuth.SignOutApi, null);

        await server.Client.PostAsJsonAsync(HostedAuth.SignInApi, new { email = Email, password = Password });
        var me = await server.Client.GetFromJsonAsync<Me>(HostedAuth.MeApi);

        Assert.Equal("Ada O'Neill-Vance", me!.Name);
    }

    /// <summary>What <see cref="HostedAuth.MeApi"/> answers. Named properties, so a rename is a red test.</summary>
    private sealed record Me(bool SignedIn, string? Email, long? UserId, string? Name, string? DisplayName);

    [Fact]
    public async Task A_configured_sign_up_code_keeps_strangers_out_of_the_owners_ai_bill()
    {
        await using var server = await StartAsync(hosted: true, signUpCode: "let-me-in");

        var withoutCode = await server.SignUpAsync(Email, Password);
        var withWrongCode = await server.SignUpAsync(Email, Password, code: "guess");
        var withCode = await server.SignUpAsync(Email, Password, code: "let-me-in");

        Assert.Equal(HttpStatusCode.Forbidden, withoutCode.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, withWrongCode.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withCode.StatusCode);
    }

    [Fact]
    public async Task Sign_up_is_open_when_no_code_is_configured()
    {
        await using var server = await StartAsync(hosted: true);

        Assert.Equal(HttpStatusCode.OK, (await server.SignUpAsync(Email, Password)).StatusCode);
    }

    [Fact]
    public async Task Who_am_I_names_the_signed_in_seller_and_nobody_else()
    {
        await using var server = await StartAsync(hosted: true);
        await server.SignUpAsync(Email, Password);

        var me = await server.Client.GetFromJsonAsync<MeResponse>(HostedAuth.MeApi);

        Assert.Equal(Email, me!.Email);
        Assert.True(me.UserId > 0);
    }

    private sealed record MeResponse(bool SignedIn, string Email, long UserId);

    // ── The allow-list, and its edges ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HostedAuth.SignInPath)]
    [InlineData(HostedAuth.SignUpPath)]
    [InlineData(HostedAuth.HealthPath)]
    [InlineData("/")]           // index.html — the app shell, which loads the JavaScript below
    [InlineData("/app.js")]
    [InlineData("/style.css")]
    [InlineData("/favicon.svg")]
    public async Task The_allow_list_answers_without_a_session(string path)
    {
        await using var server = await StartAsync(hosted: true);

        // A sign-in page that needs a sign-in to render is a locked door with the key inside.
        // None of these carry a seller's data — every byte of that arrives through an API call.
        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task The_sellers_own_photos_are_not_a_static_ui_asset()
    {
        await using var server = await StartAsync(hosted: true);

        // /photos and /generated-photos hold photographs of the seller's stock. They are served by
        // static-file middleware, which runs before routing and so is untouched by the fallback
        // policy — this is the hand-written gate that closes them anyway.
        var refused = await server.Client.GetAsync("/photos/item.txt");
        Assert.Equal(HttpStatusCode.Found, refused.StatusCode);

        await server.SignUpAsync(Email, Password);
        var allowed = await server.Client.GetAsync("/photos/item.txt");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Desktop_serves_the_photos_it_always_did()
    {
        await using var server = await StartAsync(hosted: false);

        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync("/photos/item.txt")).StatusCode);
    }

    [Fact]
    public async Task A_browser_asking_for_a_page_is_sent_to_the_sign_in_page()
    {
        await using var server = await StartAsync(hosted: true);

        var response = await server.Client.GetAsync("/photos/item.txt");

        // A redirect for something the browser navigates to; a 401 for anything under /api, which
        // fetch() callers parse as JSON and would choke on a login page returned as 200.
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.EndsWith($"{HostedAuth.SignInPath}?next=%2Fphotos%2Fitem.txt", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_seller_coming_back_from_ebay_is_shown_the_way_in_rather_than_a_blank_401()
    {
        await using var server = await StartAsync(hosted: true);

        // eBay returns the seller to /api/ebay/callback as a top-level navigation. If the session
        // expired while they were away, a bare 401 ends the OAuth round trip on a blank page.
        var navigation = new HttpRequestMessage(HttpMethod.Get, "/api/ebay/callback?code=whatever");
        navigation.Headers.Add("Sec-Fetch-Mode", "navigate");
        var response = await server.Client.SendAsync(navigation);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains(HostedAuth.SignInPath, response.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_script_asking_for_the_same_url_still_gets_a_401()
    {
        await using var server = await StartAsync(hosted: true);

        // fetch() sends cors or same-origin, never navigate. It must not be handed a login page
        // with a 200 on it — every caller in app.js would try to parse that as its own JSON.
        var call = new HttpRequestMessage(HttpMethod.Get, "/api/ebay/callback?code=whatever");
        call.Headers.Add("Sec-Fetch-Mode", "cors");
        var response = await server.Client.SendAsync(call);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_endpoint_added_later_is_closed_by_having_been_written()
    {
        await using var server = await StartAsync(hosted: true);

        // The stand-in for endpoint number 190. Nothing about it mentions authorization; it is
        // protected because the default is closed, which is the whole reason for a fallback policy
        // rather than a list of endpoints somebody has to remember to add to.
        var response = await server.Client.GetAsync("/api/listings");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Behind the reverse proxy ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task The_health_check_answers_without_a_session(string method)
    {
        await using var server = await StartAsync(hosted: true);

        var response = await server.Client.SendAsync(
            new HttpRequestMessage(new HttpMethod(method), HostedAuth.HealthPath));

        // HEAD is in here because it is what uptime monitors send, and because getting it wrong is
        // invisible: a route that matches the path but not the method selects a 405 endpoint, which
        // carries none of the real endpoint's AllowAnonymous — so the fallback policy challenges it
        // and the monitor is told to go and log in. The deployment reads as down while it is up.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_redirect_keeps_the_scheme_the_user_is_actually_on()
    {
        await using var server = await StartAsync(hosted: true);

        // TLS is terminated at the proxy, so this request reaches Kestrel over plain http and only
        // the header says otherwise. Without UseForwardedHeaders the app believes the connection it
        // can see, and sends the browser to http://…/signin.html — a cleartext hop carrying the
        // path the seller was trying to reach, on a site whose session cookie is Secure.
        var request = new HttpRequestMessage(HttpMethod.Get, "/photos/item.txt");
        request.Headers.Add("X-Forwarded-Proto", "https");
        var response = await server.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.StartsWith("https://", response.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    // ── The server under test ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The app's own auth wiring, in the app's own order, around a couple of stand-in endpoints.
    /// Kept deliberately close to Program.cs: <c>UseSignedInUser</c> before the static mounts,
    /// <c>RequireSignIn</c> after them and before anything is mapped.
    /// </summary>
    private static async Task<TestServer> StartAsync(bool hosted, string? signUpCode = null,
                                                     int? authRequestsPerWindow = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "ing-hosted-auth-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var photos = Path.Combine(root, "photos");
        Directory.CreateDirectory(photos);
        await File.WriteAllTextAsync(Path.Combine(photos, "item.txt"), "a photograph of the seller's stock");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = root,
            EnvironmentName = "Production",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        if (signUpCode is not null)
            builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { [HostedAuth.SignUpCodeSetting] = signUpCode });

        // The app resolves this from ListingDatabase; here it points at a scratch file. Registered
        // first so AddAccounts' TryAddSingleton leaves it alone.
        builder.Services.AddSingleton(new UserStore(Path.Combine(root, "users.db")));

        // Same scratch file, and the same trick — but with a clock the test can wind forward, so
        // that watching a fifteen-minute lock lift does not take fifteen minutes.
        var clock = new TestClock();
        builder.Services.AddSingleton(new SignInThrottle(Path.Combine(root, "users.db"), () => clock.Now));

        // The cookie would otherwise be marked Secure and never come back over a loopback http
        // test. It is Secure in a real hosted deployment — see AddAccounts.
        HostedAuth.AddAccounts(builder, hosted, secureCookie: false, authRequestsPerWindow);

        var app = builder.Build();
        HostedAuth.UseSignedInUser(app, hosted);

        var embedded = new EmbeddedFileProvider(typeof(HostedAuth).Assembly, "ING_eBay_AutoLister.wwwroot");
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = embedded, DefaultFileNames = ["index.html"] });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = embedded });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(photos),
            RequestPath  = "/photos",
            ServeUnknownFileTypes = true,
        });

        HostedAuth.RequireSignIn(app, hosted);
        HostedAuth.MapAccountEndpoints(app, hosted);

        app.MapGet(RepresentativeEndpoint, () => Results.Ok(new { ready = true }));
        app.MapGet("/api/listings", () => Results.Ok(new[] { "the seller's listings" }));
        // The one shape under /api that a browser navigates to rather than fetches: eBay's OAuth
        // return. Mapped here because a fallback policy only applies to a route that matched —
        // an unmapped path is a 404 and never reaches authorization at all.
        app.MapGet("/api/ebay/callback", () => Results.Ok(new { linked = true }));

        await app.StartAsync();
        return new TestServer(app, root, clock);
    }

    /// <summary>The throttle's sense of time, so a test can wind past a lockout.</summary>
    private sealed class TestClock
    {
        public DateTimeOffset Now { get; private set; } = DateTimeOffset.UtcNow;

        public void Advance(TimeSpan by) => Now += by;
    }

    private sealed class TestServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _root;
        private readonly TestClock _clock;

        public HttpClient Client { get; }

        /// <summary>Winds the throttle's clock forward. Nothing else in the app reads it.</summary>
        public void Wait(TimeSpan howLong) => _clock.Advance(howLong);

        public TestServer(WebApplication app, string root, TestClock clock)
        {
            _app   = app;
            _root  = root;
            _clock = clock;

            // A real cookie jar, and no automatic redirect-following: whether the answer is a 302
            // to the sign-in page or a 401 is half of what these tests are checking.
            Client = new HttpClient(new HttpClientHandler
            {
                UseCookies       = true,
                CookieContainer  = new CookieContainer(),
                AllowAutoRedirect = false,
            })
            {
                BaseAddress = new Uri(app.Urls.First()),
            };
        }

        /// <param name="name">
        /// Defaulted so the tests that are about something else do not each have to carry one.
        /// The tests that are about the name pass it — including the empty string.
        /// </param>
        public Task<HttpResponseMessage> SignUpAsync(string email, string password, string? code = null,
                                                     string? name = "Dana Ellis") =>
            Client.PostAsJsonAsync(HostedAuth.SignUpApi, new { email, password, code, name });

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder, not the point */ }
        }
    }
}
