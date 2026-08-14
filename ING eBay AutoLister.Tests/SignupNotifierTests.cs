using System.Net.Mail;
using ING_eBay_AutoLister.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The owner's sign-up notification: what it says, what it must never say, and what it does when a
/// hundred people sign up in ten minutes.
/// </summary>
/// <remarks>
/// No mail server is involved. The transport is replaced with a delegate that records the message,
/// which is what makes it possible to assert on the exact bytes that would have left the box — the
/// interesting claims here are all about content and count, not about SMTP.
/// </remarks>
public class SignupNotifierTests
{
    private const string OwnerAddress = "ns@ingmining.com";

    /// <summary>What was handed to the transport, read out before the sender disposes it.</summary>
    private sealed record Posted(string To, string? ReplyTo, string Subject, string Body);

    private readonly List<Posted> _sent = [];
    private readonly List<string> _bodies = [];
    private readonly List<string> _subjects = [];
    private DateTimeOffset _now = new(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);

    // ── What it says ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task It_tells_the_owner_the_four_things_and_nothing_else()
    {
        var notifier = Notifier();

        await notifier.NotifySignupAsync("newseller@example.com", "203.0.113.7", "Dana Reed");

        var body = Assert.Single(_bodies);
        Assert.Contains("Dana Reed", body, StringComparison.Ordinal);
        Assert.Contains("newseller@example.com", body, StringComparison.Ordinal);
        Assert.Contains("2026-08-14 09:00:00", body, StringComparison.Ordinal);
        Assert.Contains("203.0.113.7", body, StringComparison.Ordinal);

        // Four labelled lines, and the sentence that introduces them carries no colon. If a fifth
        // fact is ever added here, this is the test that has to be argued with first.
        Assert.Equal(4, body.Split('\n').Count(line => line.Contains(':', StringComparison.Ordinal)));
    }

    [Fact]
    public async Task It_goes_to_the_owner_and_replies_to_the_person_who_signed_up()
    {
        var notifier = Notifier();

        await notifier.NotifySignupAsync("newseller@example.com", "203.0.113.7", "Dana Reed");

        var message = Assert.Single(_sent);
        Assert.Equal(OwnerAddress, message.To);
        // The reply-to repeats the address that is already in the body, so it adds no fact — it
        // just makes answering a new seller one keystroke instead of a copy and paste.
        Assert.Equal("newseller@example.com", message.ReplyTo);
    }

    [Fact]
    public async Task The_subject_names_who_signed_up()
    {
        var notifier = Notifier();

        await notifier.NotifySignupAsync("newseller@example.com", "203.0.113.7", "Dana Reed");

        Assert.Contains("Dana Reed", Assert.Single(_subjects), StringComparison.Ordinal);
        Assert.Contains("newseller@example.com", _subjects[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sign_up_with_no_name_still_notifies()
    {
        var notifier = Notifier();

        await notifier.NotifySignupAsync("newseller@example.com", ip: null, newUserName: null);

        var body = Assert.Single(_bodies);
        Assert.Contains("newseller@example.com", body, StringComparison.Ordinal);
        // Blank fields say so rather than leaving a label with nothing after it, which reads like a
        // truncated message and sends the owner looking for a bug.
        Assert.Contains("not given", body, StringComparison.Ordinal);
    }

    // ── What it must never say ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Nothing_that_could_be_used_to_sign_in_as_them_can_reach_the_message()
    {
        var notifier = Notifier();

        // The point is structural, not textual: the password, the hash, the session cookie and
        // every eBay or Anthropic token are simply not parameters of this method, so no future edit
        // to the body can put one in a mail that crosses Gmail in the clear.
        var parameters = typeof(SignupNotifier)
            .GetMethod(nameof(SignupNotifier.NotifySignupAsync))!
            .GetParameters().Select(p => p.Name!).ToArray();

        Assert.Equal(["newUserEmail", "ip", "newUserName", "ct"], parameters);

        await notifier.NotifySignupAsync("newseller@example.com", "203.0.113.7", "Dana Reed");
        foreach (var forbidden in new[] { "password", "token", "cookie", "session", "hash", "secret" })
            Assert.DoesNotContain(forbidden, _bodies[0], StringComparison.OrdinalIgnoreCase);
    }

    // ── It never costs a sign-up ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_mail_server_that_refuses_does_not_throw_at_the_sign_up()
    {
        var notifier = new SignupNotifier(Configured(), NullLogger<SignupNotifier>.Instance,
                                          () => _now,
                                          (_, _) => throw new SmtpException("mailbox unavailable"));

        // The account already exists and the user is already signed in. A bounced notification must
        // not turn that into an error page.
        await notifier.NotifySignupAsync("newseller@example.com", "203.0.113.7", "Dana Reed");
    }

    [Fact]
    public async Task Without_smtp_configured_it_sends_nothing_at_all()
    {
        var notifier = new SignupNotifier(new ConfigurationBuilder().Build(),
                                          NullLogger<SignupNotifier>.Instance, () => _now, Record);

        await notifier.NotifySignupAsync("newseller@example.com", "203.0.113.7", "Dana Reed");

        // The desktop build and every deployment that has not set SMTP up run this code path on
        // every sign-up. Disabled has to mean silent, not "tries and logs a failure".
        Assert.Empty(_sent);
    }

    // ── A flood of sign-ups is not a flood of mail ───────────────────────────────────────────

    [Fact]
    public async Task Twenty_in_an_hour_are_all_sent()
    {
        var notifier = Notifier();

        for (var i = 0; i < SignupNotifier.MaxPerWindow; i++)
        {
            await notifier.NotifySignupAsync($"seller-{i}@example.com", "203.0.113.7", $"Seller {i}");
            _now += TimeSpan.FromMinutes(1);
        }

        Assert.Equal(SignupNotifier.MaxPerWindow, _sent.Count);
    }

    [Fact]
    public async Task The_twenty_first_is_not_sent()
    {
        var notifier = Notifier();

        for (var i = 0; i <= SignupNotifier.MaxPerWindow; i++)
            await notifier.NotifySignupAsync($"seller-{i}@example.com", "203.0.113.7", $"Seller {i}");

        // Gmail's answer to a few hundred messages in an hour is to block the account, which would
        // cost the owner the notifications that actually mattered.
        Assert.Equal(SignupNotifier.MaxPerWindow, _sent.Count);
        Assert.DoesNotContain(_bodies, b => b.Contains($"seller-{SignupNotifier.MaxPerWindow}@example.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task What_the_cap_held_back_arrives_as_one_summary_at_the_end_of_the_hour()
    {
        var notifier = Notifier();
        for (var i = 0; i < SignupNotifier.MaxPerWindow + 5; i++)
            await notifier.NotifySignupAsync($"seller-{i}@example.com", "203.0.113.7", $"Seller {i}");

        _now += SignupNotifier.Window + TimeSpan.FromMinutes(1);
        await notifier.FlushDueSummaryAsync();

        Assert.Equal(SignupNotifier.MaxPerWindow + 1, _sent.Count);
        var summary = _bodies[^1];
        Assert.Contains("rate-limited", summary, StringComparison.Ordinal);
        Assert.Contains("5 more were not", summary, StringComparison.Ordinal);
        // The five it stood in for are named, with the same four facts each.
        foreach (var i in Enumerable.Range(SignupNotifier.MaxPerWindow, 5))
            Assert.Contains($"seller-{i}@example.com", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_summary_is_sent_once_however_often_it_is_asked_for()
    {
        var notifier = Notifier();
        for (var i = 0; i < SignupNotifier.MaxPerWindow + 5; i++)
            await notifier.NotifySignupAsync($"seller-{i}@example.com", "203.0.113.7", $"Seller {i}");

        _now += SignupNotifier.Window + TimeSpan.FromMinutes(1);
        await notifier.FlushDueSummaryAsync();
        await notifier.FlushDueSummaryAsync();
        await notifier.FlushDueSummaryAsync();

        // The timer and the next sign-up can both reach the flush. "A single summary" has to mean
        // one message, not one per thing that noticed the window was over.
        Assert.Equal(SignupNotifier.MaxPerWindow + 1, _sent.Count);
    }

    [Fact]
    public async Task A_flood_does_not_become_a_summary_nobody_can_read()
    {
        var notifier = Notifier();
        for (var i = 0; i < 500; i++)
            await notifier.NotifySignupAsync($"seller-{i}@example.com", "203.0.113.7", $"Seller {i}");

        _now += SignupNotifier.Window + TimeSpan.FromMinutes(1);
        await notifier.FlushDueSummaryAsync();

        var summary = _bodies[^1];
        Assert.Contains("480 more were not", summary, StringComparison.Ordinal);
        // Listed to a bound and counted past it. An unbounded list is both a mail nobody reads and
        // a slice of memory whoever is signing up chooses the size of.
        Assert.Contains("and 430 more, not listed", summary, StringComparison.Ordinal);
        Assert.Equal(50, summary.Split('\n').Count(line => line.Contains("@example.com", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_next_sign_up_after_the_hour_carries_the_summary_out()
    {
        var notifier = Notifier();
        for (var i = 0; i < SignupNotifier.MaxPerWindow + 3; i++)
            await notifier.NotifySignupAsync($"seller-{i}@example.com", "203.0.113.7", $"Seller {i}");

        _now += SignupNotifier.Window + TimeSpan.FromMinutes(1);
        await notifier.NotifySignupAsync("later@example.com", "203.0.113.9", "Later Seller");

        // The summary first, then the sign-up that closed the window.
        Assert.Equal(SignupNotifier.MaxPerWindow + 2, _sent.Count);
        Assert.Contains("rate-limited", _bodies[^2], StringComparison.Ordinal);
        Assert.Contains("later@example.com", _bodies[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_summary_counts_against_the_new_hour()
    {
        var notifier = Notifier();
        for (var i = 0; i < SignupNotifier.MaxPerWindow + 3; i++)
            await notifier.NotifySignupAsync($"seller-{i}@example.com", "203.0.113.7", $"Seller {i}");

        _now += SignupNotifier.Window + TimeSpan.FromMinutes(1);
        _sent.Clear();
        _bodies.Clear();
        for (var i = 0; i < 40; i++)
            await notifier.NotifySignupAsync($"next-{i}@example.com", "203.0.113.7", $"Next {i}");

        // Otherwise the hour that starts with a summary is a cap of twenty-one, and a flood spread
        // across window boundaries buys an extra message every hour it goes on.
        Assert.Equal(SignupNotifier.MaxPerWindow, _sent.Count);
    }

    [Fact]
    public async Task A_new_hour_starts_the_count_again()
    {
        var notifier = Notifier();
        for (var i = 0; i < SignupNotifier.MaxPerWindow; i++)
            await notifier.NotifySignupAsync($"seller-{i}@example.com", "203.0.113.7", $"Seller {i}");
        Assert.Equal(0, notifier.RemainingThisWindow);

        _now += SignupNotifier.Window + TimeSpan.FromSeconds(1);

        // Nothing was suppressed, so the new hour opens with the full allowance and no summary.
        await notifier.NotifySignupAsync("tomorrow@example.com", "203.0.113.7", "Tomorrow");
        Assert.Equal(SignupNotifier.MaxPerWindow + 1, _sent.Count);
        Assert.Contains("tomorrow@example.com", _bodies[^1], StringComparison.Ordinal);
        Assert.DoesNotContain("rate-limited", _bodies[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_quiet_hour_owes_no_summary()
    {
        var notifier = Notifier();
        await notifier.NotifySignupAsync("newseller@example.com", "203.0.113.7", "Dana Reed");

        _now += SignupNotifier.Window + TimeSpan.FromMinutes(1);
        await notifier.FlushDueSummaryAsync();

        Assert.Single(_sent);
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────

    private SignupNotifier Notifier() =>
        new(Configured(), NullLogger<SignupNotifier>.Instance, () => _now, Record);

    private Task Record(MailMessage message, CancellationToken ct)
    {
        // Copied out here because the sender disposes the message the moment this returns.
        _sent.Add(new Posted(string.Join(",", message.To.Select(a => a.Address)),
                             message.ReplyToList.FirstOrDefault()?.Address,
                             message.Subject, message.Body));
        _bodies.Add(message.Body);
        _subjects.Add(message.Subject);
        return Task.CompletedTask;
    }

    private static IConfiguration Configured() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notify:Smtp:User"]     = "knn3sales@example.com",
            ["Notify:Smtp:Password"] = "an-app-password",
            ["Notify:Smtp:To"]       = OwnerAddress,
        }).Build();
}
