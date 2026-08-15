using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The list of sessions the server will honour, on its own. The end-to-end proof that the cookie
/// handler consults it — which is the part that actually protects anybody — is in
/// <see cref="SessionLifetimeTests"/>.
/// </summary>
public class SessionStoreTests
{
    [Fact]
    public void An_issued_session_is_live_for_the_account_it_was_issued_to()
    {
        var sessions = New(out _);

        var id = sessions.Issue(userId: 7);

        Assert.True(sessions.IsLive(id, 7));
    }

    [Fact]
    public void A_session_is_not_live_for_a_different_account()
    {
        // Both halves come out of one encrypted ticket so they cannot disagree by accident. The
        // check is here because the whole point of this class is to stop believing the ticket.
        var sessions = New(out _);

        var id = sessions.Issue(userId: 7);

        Assert.False(sessions.IsLive(id, 8));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a-session-id-that-was-never-issued")]
    public void A_session_nobody_issued_is_not_live(string? id)
    {
        var sessions = New(out _);

        Assert.False(sessions.IsLive(id, 7));
    }

    [Fact]
    public void Every_sign_in_gets_an_identifier_of_its_own()
    {
        // The fixation guarantee at its root: the identifier is minted here, from the CSPRNG, and
        // is never taken from anything the caller sent.
        var sessions = New(out _);

        var ids = Enumerable.Range(0, 50).Select(_ => sessions.Issue(userId: 7)).ToList();

        Assert.Equal(50, ids.Distinct().Count());
    }

    [Fact]
    public void A_revoked_session_stops_being_live()
    {
        var sessions = New(out _);
        var id = sessions.Issue(userId: 7);

        sessions.Revoke(id);

        Assert.False(sessions.IsLive(id, 7));
    }

    [Fact]
    public void Revoking_one_session_leaves_the_accounts_others_alone()
    {
        // Signing out of the library computer must not sign the seller out of the phone in their
        // pocket. That is what makes it a thing people actually press.
        var sessions = New(out _);
        var library  = sessions.Issue(userId: 7);
        var phone    = sessions.Issue(userId: 7);

        sessions.Revoke(library);

        Assert.False(sessions.IsLive(library, 7));
        Assert.True(sessions.IsLive(phone, 7));
    }

    [Fact]
    public void Revoking_everything_for_an_account_is_what_a_stolen_password_needs()
    {
        var sessions = New(out _);
        var first    = sessions.Issue(userId: 7);
        var second   = sessions.Issue(userId: 7);
        var stranger = sessions.Issue(userId: 8);

        Assert.Equal(2, sessions.RevokeAllFor(7));

        Assert.False(sessions.IsLive(first, 7));
        Assert.False(sessions.IsLive(second, 7));
        Assert.True(sessions.IsLive(stranger, 8));
    }

    [Fact]
    public void A_session_expires_absolutely_however_much_it_is_used()
    {
        // The cookie's own sliding fortnight can be extended forever by a browser that keeps
        // calling. This cannot, so a stolen cookie has an end date even if the thief is careful.
        var sessions = New(out var clock, lifetime: TimeSpan.FromDays(30));
        var id = sessions.Issue(userId: 7);

        clock.Advance(TimeSpan.FromDays(29));
        sessions.Touch(id);
        Assert.True(sessions.IsLive(id, 7));

        clock.Advance(TimeSpan.FromDays(2));
        sessions.Touch(id);
        Assert.False(sessions.IsLive(id, 7));
    }

    [Fact]
    public void The_table_does_not_grow_forever()
    {
        // Every sign-in writes a row. Without the sweep the table is a permanent record of every
        // sign-in the deployment has ever seen, on the same disk as the seller's data.
        var sessions = New(out var clock, lifetime: TimeSpan.FromDays(30));
        for (var i = 0; i < 20; i++) sessions.Issue(userId: 7);
        Assert.Equal(20, sessions.CountRows());

        clock.Advance(TimeSpan.FromDays(31));
        sessions.Issue(userId: 7);

        // The twenty dead ones are gone; the one just issued is not.
        Assert.Equal(1, sessions.CountRows());
    }

    [Fact]
    public void A_live_session_is_never_swept_up()
    {
        var sessions = New(out var clock, lifetime: TimeSpan.FromDays(30));
        var old = sessions.Issue(userId: 7);

        clock.Advance(TimeSpan.FromDays(31));
        var fresh = sessions.Issue(userId: 7);
        clock.Advance(TimeSpan.FromDays(1));

        Assert.False(sessions.IsLive(old, 7));
        Assert.True(sessions.IsLive(fresh, 7));
    }

    private static SessionStore New(out TestClock clock, TimeSpan? lifetime = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "ing-session-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var theClock = new TestClock();
        clock = theClock;
        return new SessionStore(Path.Combine(root, "sessions.db"), () => theClock.Now, lifetime);
    }

    private sealed class TestClock
    {
        public DateTimeOffset Now { get; private set; } = DateTimeOffset.UtcNow;
        public void Advance(TimeSpan howLong) => Now += howLong;
    }
}
