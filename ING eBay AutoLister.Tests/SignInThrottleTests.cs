using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The counter behind the lockout, on its own. <see cref="HostedAuthTests"/> proves the sign-in
/// endpoint refuses a locked account over real HTTP; this proves the arithmetic underneath it —
/// when the lock engages, when it lifts, what resets it, and what does not.
/// </summary>
/// <remarks>
/// The clock is injected rather than waited on. A test that proved a fifteen-minute lock lifts by
/// sleeping for fifteen minutes is a test nobody runs.
/// </remarks>
public class SignInThrottleTests : IDisposable
{
    private const string Email = "seller@example.com";

    private readonly string _root;
    private DateTimeOffset _now = new(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);

    public SignInThrottleTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ing-throttle-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder, not the point */ }
        GC.SuppressFinalize(this);
    }

    // ── The lock ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Four_misses_leave_the_account_open()
    {
        var throttle = Throttle();

        for (var i = 0; i < SignInThrottle.MaxFailures - 1; i++)
            Assert.False(throttle.RecordFailure(Email).Locked);

        Assert.False(throttle.Check(Email).Locked);
    }

    [Fact]
    public void The_fifth_miss_locks_the_account()
    {
        var throttle = Throttle();

        for (var i = 0; i < SignInThrottle.MaxFailures; i++) throttle.RecordFailure(Email);

        var standing = throttle.Check(Email);
        Assert.True(standing.Locked);
        Assert.Equal(_now + SignInThrottle.DefaultLockout, standing.Until);
    }

    [Fact]
    public void The_lock_lifts_when_the_window_is_over()
    {
        var throttle = Throttle();
        for (var i = 0; i < SignInThrottle.MaxFailures; i++) throttle.RecordFailure(Email);

        _now += SignInThrottle.DefaultLockout - TimeSpan.FromSeconds(1);
        Assert.True(throttle.Check(Email).Locked);

        _now += TimeSpan.FromSeconds(2);
        Assert.False(throttle.Check(Email).Locked);
    }

    [Fact]
    public void A_lock_that_has_lifted_starts_the_count_again_from_nothing()
    {
        var throttle = Throttle();
        for (var i = 0; i < SignInThrottle.MaxFailures; i++) throttle.RecordFailure(Email);
        _now += SignInThrottle.DefaultLockout + TimeSpan.FromMinutes(1);

        // Not "one more miss and you are locked again for another quarter of an hour". The count
        // is consecutive-since-the-lock-lifted, so someone who comes back and mistypes once has
        // four tries left like anyone else.
        Assert.False(throttle.RecordFailure(Email).Locked);
        Assert.Equal(1, throttle.Check(Email).Failures);
    }

    [Fact]
    public void Attempts_during_a_lock_do_not_extend_it()
    {
        var throttle = Throttle();
        for (var i = 0; i < SignInThrottle.MaxFailures; i++) throttle.RecordFailure(Email);
        var lifts = throttle.Check(Email).Until;

        // A script that keeps hammering while the lock is on would otherwise hold a real seller
        // out of their own account for as long as it cared to keep going.
        _now += TimeSpan.FromMinutes(5);
        for (var i = 0; i < 50; i++) throttle.RecordFailure(Email);

        Assert.Equal(lifts, throttle.Check(Email).Until);
    }

    // ── What clears it ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_sign_in_that_works_clears_the_count()
    {
        var throttle = Throttle();
        for (var i = 0; i < SignInThrottle.MaxFailures - 1; i++) throttle.RecordFailure(Email);

        throttle.RecordSuccess(Email);

        // Four typos then the right password must not leave the seller one mistake from a lockout.
        Assert.Equal(0, throttle.Check(Email).Failures);
        Assert.False(throttle.RecordFailure(Email).Locked);
    }

    [Fact]
    public void Misses_spread_across_days_are_not_consecutive()
    {
        var throttle = Throttle();

        for (var i = 0; i < SignInThrottle.MaxFailures * 3; i++)
        {
            Assert.False(throttle.RecordFailure(Email).Locked);
            _now += TimeSpan.FromHours(6);
        }
    }

    // ── What it is keyed on ──────────────────────────────────────────────────────────────────

    [Fact]
    public void One_address_typed_two_ways_is_one_count()
    {
        var throttle = Throttle();

        throttle.RecordFailure("seller@example.com");
        throttle.RecordFailure("  Seller@Example.COM  ");
        throttle.RecordFailure("SELLER@EXAMPLE.COM");
        throttle.RecordFailure("seller@example.com");
        throttle.RecordFailure("Seller@example.com");

        // Otherwise five tries becomes five tries per capitalisation, which is as many as anyone
        // needs.
        Assert.True(throttle.Check(Email).Locked);
    }

    [Fact]
    public void Locking_one_account_does_not_touch_another()
    {
        var throttle = Throttle();
        for (var i = 0; i < SignInThrottle.MaxFailures; i++) throttle.RecordFailure(Email);

        Assert.True(throttle.Check(Email).Locked);
        Assert.False(throttle.Check("someone-else@example.com").Locked);
    }

    [Fact]
    public void An_address_that_never_signed_up_locks_exactly_like_one_that_did()
    {
        var throttle = Throttle();

        for (var i = 0; i < SignInThrottle.MaxFailures; i++) throttle.RecordFailure("nobody@example.com");

        // The throttle never asks the users table anything. If it only locked real addresses, the
        // sixth attempt would answer "locked" for a registered address and "wrong password" for an
        // invented one — which is a way to ask which addresses are registered, the exact question
        // UserStore.Verify and PasswordHash.VerifyNothing exist to refuse.
        Assert.True(throttle.Check("nobody@example.com").Locked);
    }

    [Fact]
    public void Rubbish_in_the_email_box_is_counted_too()
    {
        var throttle = Throttle();

        for (var i = 0; i < SignInThrottle.MaxFailures; i++) throttle.RecordFailure("not-an-address");

        Assert.True(throttle.Check("not-an-address").Locked);
        Assert.False(throttle.Check("also-not-an-address").Locked);
    }

    // ── It survives the thing that clears a counter kept in memory ───────────────────────────

    [Fact]
    public void A_restart_does_not_hand_the_attacker_five_more_tries()
    {
        var path = Path.Combine(_root, "users.db");
        var before = new SignInThrottle(path, () => _now);
        for (var i = 0; i < SignInThrottle.MaxFailures; i++) before.RecordFailure(Email);

        // A hosted deployment restarts on every deploy and every reboot. An in-memory counter is
        // one that anything with a restart in it quietly resets.
        var after = new SignInThrottle(path, () => _now);

        Assert.True(after.Check(Email).Locked);
    }

    // ── The table does not grow forever ──────────────────────────────────────────────────────

    [Fact]
    public void Addresses_nobody_is_guessing_at_any_more_are_swept_up()
    {
        var throttle = Throttle();

        // A spray types a different address every time, and every one of them writes a row. Left
        // alone, the table becomes a list of every address anybody has ever guessed at, kept
        // forever, on the same disk as the database.
        for (var i = 0; i < 200; i++) throttle.RecordFailure($"sprayed-{i}@example.com");
        Assert.Equal(200, throttle.CountedAddresses());

        _now += SignInThrottle.DefaultLockout + TimeSpan.FromMinutes(1);
        throttle.RecordFailure("someone-real@example.com");

        // Only the row that is still inside the window. Nothing was decided differently — a row
        // that old was already being read as a fresh start.
        Assert.Equal(1, throttle.CountedAddresses());
    }

    [Fact]
    public void The_sweep_does_not_cost_anyone_the_count_they_are_partway_through()
    {
        var throttle = Throttle();
        for (var i = 0; i < 200; i++) throttle.RecordFailure($"sprayed-{i}@example.com");

        // Far enough on that the next failure triggers a sweep. The four misses below all land
        // after it, so a sweep that took live counters with it would leave the fifth unlocked —
        // and the sweep would be a way to reset the lockout by making noise first.
        _now += SignInThrottle.DefaultLockout + TimeSpan.FromMinutes(1);
        for (var i = 0; i < SignInThrottle.MaxFailures - 1; i++)
            Assert.False(throttle.RecordFailure(Email).Locked);

        Assert.True(throttle.RecordFailure(Email).Locked);
    }

    // ── What it tells the person ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_message_says_it_is_locked_and_when_it_lifts()
    {
        var message = SignInThrottle.LockedMessage(TimeSpan.FromMinutes(14.2));

        Assert.Contains("locked", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("15 minutes", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_last_minute_of_a_lock_is_not_reported_as_zero_minutes()
    {
        // Rounding down would tell someone with forty seconds left to "try again in 0 minutes".
        Assert.Contains("another minute", SignInThrottle.LockedMessage(TimeSpan.FromSeconds(40)),
                        StringComparison.Ordinal);
    }

    private SignInThrottle Throttle() => new(Path.Combine(_root, "users.db"), () => _now);
}
