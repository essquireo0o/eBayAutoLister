using System.Diagnostics;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The owner dashboard's key. A guessable admin key is worse than no admin key, because it is the
/// one URL that reads across every account.
/// </summary>
public class AdminKeyTests
{
    [Fact]
    public void The_right_key_matches_and_nothing_else_does()
    {
        var store = New();
        var key   = store.EnsureAdminKey();

        Assert.True(store.AdminKeyMatches(key));
        Assert.False(store.AdminKeyMatches(key + "0"));
        Assert.False(store.AdminKeyMatches(key[..^1]));
        Assert.False(store.AdminKeyMatches(key.ToUpperInvariant()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_key_is_never_a_match(string? offered)
    {
        // Worth its own test because the obvious refactor — comparing the two strings and nothing
        // else — makes a blank key match a deployment whose key has not been generated yet.
        var store = New();

        Assert.False(store.AdminKeyMatches(offered));
    }

    [Fact]
    public void The_generated_key_is_long_and_random()
    {
        var first  = New().EnsureAdminKey();
        var second = New().EnsureAdminKey();

        Assert.Equal(32, first.Length);            // 128 bits, hex
        Assert.NotEqual(first, second);
        Assert.All(first, c => Assert.Contains(c, "0123456789abcdef"));
    }

    [Fact]
    public void The_same_deployment_keeps_the_same_key()
    {
        var store = New();

        Assert.Equal(store.EnsureAdminKey(), store.EnsureAdminKey());
    }

    [Fact]
    public void A_wrong_key_that_shares_a_prefix_costs_the_same_to_refuse_as_one_that_shares_nothing()
    {
        // The attack a plain != allows: string comparison returns at the first differing byte, so
        // a near-miss is measurably slower to refuse than a wild miss. That turns 2^128 into
        // thirty-two rounds of sixteen guesses. This asserts the difference is in the noise.
        var store = New();
        var key   = store.EnsureAdminKey();

        var almost  = key[..^1] + (key[^1] == 'a' ? 'b' : 'a');   // differs in the last character
        var nothing = (key[0] == '0' ? '1' : '0') + key[1..];     // differs in the first

        var almostTime  = TimeOf(() => store.AdminKeyMatches(almost));
        var nothingTime = TimeOf(() => store.AdminKeyMatches(nothing));

        // Generous on purpose. A timing test on a shared CI box cannot be tight, and it does not
        // need to be: the failure this catches is an early-return compare, where the gap grows
        // with the length of the shared prefix rather than staying inside a ratio like this.
        var ratio = (double)Math.Max(almostTime, nothingTime) / Math.Max(1, Math.Min(almostTime, nothingTime));
        Assert.True(ratio < 5, $"near-miss {almostTime} ticks vs wild-miss {nothingTime} ticks");
    }

    private static long TimeOf(Func<bool> compare)
    {
        // Warm up, so the first call's JIT is not the measurement.
        for (var i = 0; i < 200; i++) compare();

        // The FASTEST of several rounds, not one round. Interference on this box is entirely
        // one-sided — another session's build or test run can only ever make a round slower,
        // never faster — so a single timing is a measurement of the machine's mood, and this
        // test went red twice in three runs while two other sessions were compiling. The
        // minimum is the round that got the least interference, which is the closest thing to
        // the compare's own cost. The property under test is unaffected: an early-return
        // compare is slower on a shared prefix in EVERY round, including its best one.
        var best = long.MaxValue;
        for (var round = 0; round < 5; round++)
        {
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 4_000; i++) compare();
            best = Math.Min(best, watch.ElapsedTicks);
        }
        return best;
    }

    private static CredentialsStore New()
    {
        var root = Path.Combine(Path.GetTempPath(), "ing-admin-key-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new CredentialsStore(Path.Combine(root, "credentials.json"));
    }
}
