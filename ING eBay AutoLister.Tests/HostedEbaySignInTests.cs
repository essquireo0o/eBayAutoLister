using System.Net;
using ING_eBay_AutoLister.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The eBay sign-in on a server that has many sellers on it, driven through the real service.
/// </summary>
/// <remarks>
/// <para>
/// Two things have to be true at once and neither is visible from the other's tests. The sign-in
/// has to come <i>back</i> — the relay's last hop was hardcoded to <c>localhost:9332</c>, which on a
/// hosted deployment is a port on the seller's own machine with nothing listening on it — and when
/// it does, the tokens have to land in the row of the person who started it and nowhere else.
/// </para>
/// <para>
/// The second one is the expensive half. An access token and a refresh token are one seller's
/// eighteen-month grant to list and sell on their eBay account; a hosted build that wrote them into
/// the single credentials record the desktop build has always had would hand the first person to
/// connect eBay to everybody who signed up after them, and it would do it silently, looking exactly
/// like a sign-in that worked. <see cref="Two_sellers_who_each_connect_ebay_get_two_separate_grants"/>
/// is what fails.
/// </para>
/// </remarks>
public class HostedEbaySignInTests : IDisposable
{
    private const string OwnerClientId = "INGListi-hosted-PRD-0000000000-0000aaaa";
    private const string OwnerSecret   = "PRD-0000aaaa1111-2222-3333-4444";
    private const string OwnerRuName   = "Nicholas_Squire-Nicholas-AutoLi-0000000";

    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), "ing-hosted-signin", Guid.NewGuid().ToString("N"));

    public HostedEbaySignInTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* a temp folder, not the point */ }
        GC.SuppressFinalize(this);
    }

    private static string TokensFor(string who) =>
        $$"""
        {"access_token":"access-{{who}}","refresh_token":"refresh-{{who}}",
         "expires_in":7200,"refresh_token_expires_in":47304000,"token_type":"User Access Token"}
        """;

    // ── The letter that decides where the sign-in comes back to ──────────────────────────────

    [Fact]
    public void A_hosted_sign_in_tells_the_relay_to_return_to_the_hosted_site()
    {
        var state = StateSentToEbay(HostedService(out _, out _));

        Assert.EndsWith("h", state, StringComparison.Ordinal);
        // And what is in front of the letter is still exactly what the relay's own regular
        // expression accepts — ^([0-9a-f]{32})([a-z]?)$ — because it strips the letter and uses
        // the rest as the key it stored the tokens under.
        Assert.Matches("^[0-9a-f]{32}h$", state);
    }

    /// <summary>
    /// The other half of the same fact, and the one that must never be "fixed" by making the
    /// desktop match the hosted build. Every installed copy of the desktop app signs in through
    /// this same relay, and a bare state is what routes it back to the seller's own machine.
    /// </summary>
    [Fact]
    public void A_desktop_sign_in_sends_the_bare_state_it_always_has()
    {
        var state = StateSentToEbay(DesktopService(out _, out _));

        Assert.Matches("^[0-9a-f]{32}$", state);
        Assert.Equal("", EbayRelayReturn.Desktop.Suffix);
    }

    /// <summary>
    /// The suffix is for eBay and the relay. The relay stores the tokens under the bare session and
    /// its pickup endpoint rejects anything that is not 32 hex characters outright — so sending the
    /// suffixed form to claim them would fail the sign-in on its very last hop, with HTTP 400
    /// reported as "the relay refused the pickup" and nothing naming the letter that caused it.
    /// </summary>
    [Fact]
    public void The_tokens_are_claimed_with_the_bare_session_not_the_suffixed_state()
    {
        var url = EbayRelayPickup.Url("0123456789abcdef0123456789abcdefh", "a-pickup-token");

        var query = QueryHelpers.ParseQuery(new Uri(url).Query);
        Assert.Equal("0123456789abcdef0123456789abcdef", query["session"].ToString());
        Assert.Matches("^[0-9a-f]{32}$", query["session"].ToString());
    }

    /// <summary>
    /// The relay strips the suffix itself, so <c>/api/ebay/finish</c> is handed the bare session —
    /// but eBay's <i>direct</i> callback echoes state back exactly as it was sent. Both forms have
    /// to find the same ledger entry, or a hosted sign-in that came back the direct way would be
    /// refused as one this app never started.
    /// </summary>
    [Fact]
    public async Task A_state_that_comes_back_with_its_letter_still_finds_the_sign_in_it_belongs_to()
    {
        var ebay = HostedService(out var seller, out var handler);
        handler.Then(HttpStatusCode.OK, TokensFor("one"));
        seller.Id = 1;

        var state = StateSentToEbay(ebay);
        Assert.EndsWith("h", state, StringComparison.Ordinal);

        var status = await ebay.CompleteRelaySignInAsync(state, "pickup-1");

        Assert.Equal(EbaySignInStage.Connected, status.Stage);
        Assert.Equal("connected", status.Code);
    }

    [Fact]
    public void Only_a_trailing_letter_is_stripped_and_nothing_else_is_touched()
    {
        Assert.Equal("0123456789abcdef0123456789abcdef",
            EbayRelayReturn.SessionFrom("0123456789abcdef0123456789abcdefh"));
        Assert.Equal("0123456789abcdef0123456789abcdef",
            EbayRelayReturn.SessionFrom("0123456789abcdef0123456789abcdef"));

        // Anything that is not a session with a letter on it is handed back untouched, so a forged
        // or stale state fails the ledger check on its own merits rather than being trimmed into
        // something that might match.
        Assert.Equal("not-a-session", EbayRelayReturn.SessionFrom("not-a-session"));
        Assert.Equal("0123456789abcdef0123456789abcdefhh", EbayRelayReturn.SessionFrom("0123456789abcdef0123456789abcdefhh"));
        Assert.Equal("", EbayRelayReturn.SessionFrom(null));
    }

    // ── The tokens, which belong to one person each ──────────────────────────────────────────

    /// <summary>
    /// The single most dangerous line in the feature, proved end to end: two sellers each finish a
    /// real relay sign-in through the same singleton <see cref="EbayService"/> — which is how the
    /// app runs it — and end up with two grants that cannot see each other.
    /// </summary>
    [Fact]
    public async Task Two_sellers_who_each_connect_ebay_get_two_separate_grants()
    {
        var ebay = HostedService(out var seller, out var handler);
        handler.Then(HttpStatusCode.OK, TokensFor("one"))
               .Then(HttpStatusCode.OK, TokensFor("two"));

        // Seller 1 signs in, in their own browser, on their own request.
        seller.Id = 1;
        var first = await ebay.CompleteRelaySignInAsync(StateSentToEbay(ebay), "pickup-1");
        Assert.Equal(EbaySignInStage.Connected, first.Stage);

        // Seller 2 does the same thing five minutes later, against the same process.
        seller.Id = 2;
        var second = await ebay.CompleteRelaySignInAsync(StateSentToEbay(ebay), "pickup-2");
        Assert.Equal(EbaySignInStage.Connected, second.Stage);

        // Two grants, and each seller reads their own.
        seller.Id = 1;
        Assert.Equal("access-one",  seller.Store.GetUserToken());
        Assert.Equal("refresh-one", seller.Store.GetRefreshToken());

        seller.Id = 2;
        Assert.Equal("access-two",  seller.Store.GetUserToken());
        Assert.Equal("refresh-two", seller.Store.GetRefreshToken());

        // Neither can reach the other's. This is the assertion the whole file is for: if the
        // tokens had gone into one shared record, the second sign-in would have overwritten the
        // first and BOTH of these would read "refresh-two".
        seller.Id = 1;
        Assert.NotEqual("refresh-two", seller.Store.GetRefreshToken());
        Assert.NotEqual("access-two",  seller.Store.GetUserToken());

        // And a third person who has connected nothing has nothing — not somebody else's grant.
        seller.Id = 3;
        Assert.Equal("", seller.Store.GetRefreshToken());
        Assert.Equal("", seller.Store.GetUserToken());
    }

    /// <summary>
    /// Same claim, one level down: what is on disk. Reading a refresh token out of a stolen
    /// database file is the same outcome as reading it out of another user's session.
    /// </summary>
    [Fact]
    public async Task Neither_sellers_grant_is_in_the_database_in_the_clear()
    {
        var ebay = HostedService(out var seller, out var handler);
        handler.Then(HttpStatusCode.OK, TokensFor("one"))
               .Then(HttpStatusCode.OK, TokensFor("two"));

        seller.Id = 1;
        await ebay.CompleteRelaySignInAsync(StateSentToEbay(ebay), "pickup-1");
        seller.Id = 2;
        await ebay.CompleteRelaySignInAsync(StateSentToEbay(ebay), "pickup-2");

        var rowOne = seller.Stored.ReadStoredText(1)!;
        var rowTwo = seller.Stored.ReadStoredText(2)!;

        Assert.DoesNotContain("refresh-one", rowOne, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-two", rowTwo, StringComparison.Ordinal);
        // Two rows, and not one row twice.
        Assert.NotEqual(rowOne, rowTwo);
    }

    /// <summary>
    /// A sign-in that finishes with nobody signed in. The per-user store drops the write on
    /// purpose — there is no row to put a grant in and picking one would be how a stranger ends up
    /// listing on somebody's account — so the only wrong answer left is calling it Connected. The
    /// relay's pickup is one-time: by this point the tokens are gone, and a page saying the account
    /// is linked when nothing was kept is a seller who finds out weeks later, at a publish.
    /// </summary>
    [Fact]
    public async Task A_sign_in_with_nobody_signed_in_is_refused_rather_than_reported_as_connected()
    {
        var ebay = HostedService(out var seller, out var handler);
        handler.Then(HttpStatusCode.OK, TokensFor("nobody"));

        seller.Id = 4;                       // signed in far enough to start a sign-in…
        var state = StateSentToEbay(ebay);
        seller.Id = null;                    // …and the session is gone by the time it comes back.

        var status = await ebay.CompleteRelaySignInAsync(state, "pickup-1");

        Assert.Equal(EbaySignInStage.Failed, status.Stage);
        Assert.Equal("tokens_not_stored", status.Code);
        Assert.NotEmpty(status.NextAction);

        // And nothing was written anywhere. Not into user 4's row, not into anybody else's.
        seller.Id = 4;
        Assert.Equal("", seller.Store.GetRefreshToken());
        Assert.Null(seller.Stored.ReadStoredText(4));
    }

    // ── The desktop build, which none of this may disturb ────────────────────────────────────

    [Fact]
    public async Task The_desktop_sign_in_still_stores_its_one_sellers_tokens_in_its_one_file()
    {
        var ebay = DesktopService(out var store, out var handler);
        // Same relay answer the hosted tests get, so what differs is the build and nothing else.
        handler.Then(HttpStatusCode.OK, TokensFor("only"));

        var state = StateSentToEbay(ebay);
        Assert.Matches("^[0-9a-f]{32}$", state);

        var status = await ebay.CompleteRelaySignInAsync(state, "pickup-1");

        Assert.Equal(EbaySignInStage.Connected, status.Stage);
        Assert.Equal("refresh-only", store.GetRefreshToken());
        Assert.Equal("access-only",  store.GetUserToken());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Starts a sign-in the way the seller does, and returns the <c>state</c> eBay is sent.</summary>
    private static string StateSentToEbay(EbayService ebay)
    {
        var result = ebay.CreateAuthorizationUrl();
        Assert.True(result.Ok, result.Problem?.Reason);
        return QueryHelpers.ParseQuery(new Uri(result.Url!).Query)["state"].ToString();
    }

    /// <summary>Who the request in flight belongs to, and the per-user store that answers for them.</summary>
    private sealed class Seller
    {
        public long? Id { get; set; }
        public required UserCredentialsStore Stored { get; init; }
        public required CredentialsStore Store { get; init; }
    }

    /// <summary>
    /// The hosted build: one <see cref="EbayService"/> for the process, credentials resolved per
    /// request, and the deployment's own eBay application laid over every user's empty row.
    /// </summary>
    private EbayService HostedService(out Seller seller, out ScriptedHttpHandler handler)
    {
        var server = ServerCredentials.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Credentials:EbayClientId"]     = OwnerClientId,
                ["Credentials:EbayClientSecret"] = OwnerSecret,
                ["Credentials:EbayRuName"]       = OwnerRuName,
            }).Build());

        var stored = new UserCredentialsStore(
            Path.Combine(_scratch, "hosted.db"),
            CredentialCipher.FromKeyMaterial("a-scratch-deployment-secret"));

        Seller? current = null;
        var store = new CredentialsStore(
            new PerUserCredentialsSource(stored, () => current!.Id, server));

        current = new Seller { Stored = stored, Store = store };
        seller = current;

        handler = new ScriptedHttpHandler();
        return new EbayService(store, new StubHttpClientFactory(handler), new ActionLog(),
            relayReturn: EbayRelayReturn.Hosted);
    }

    /// <summary>The desktop build, unchanged: one credentials.json, one seller, no suffix.</summary>
    private EbayService DesktopService(out CredentialsStore store, out ScriptedHttpHandler handler)
    {
        store = new CredentialsStore(Path.Combine(_scratch, "credentials.json"));
        store.Save(new CredentialsPatch
        {
            EbayClientId     = "ING-PRD-id",
            EbayClientSecret = "PRD-secret",
            EbayRuName       = "ING_Mining-INGPRDid-abc123",
        });

        handler = new ScriptedHttpHandler();
        // No relayReturn at all — the argument every existing caller omits, and the default it
        // falls back to is what the desktop build has always sent.
        return new EbayService(store, new StubHttpClientFactory(handler), new ActionLog());
    }
}
