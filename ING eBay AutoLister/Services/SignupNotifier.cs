using System.Net;
using System.Net.Mail;
using System.Text;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// One sign-up, in the four facts the owner is told about it.
/// </summary>
/// <param name="Name">What they said they are called. Blank for a sign-up that predates the field.</param>
/// <param name="Email">The address they signed up with.</param>
/// <param name="AtUtc">When, in UTC.</param>
/// <param name="Ip">The address the sign-up came from, as far as the proxy in front reported it.</param>
public sealed record SignupNotice(string? Name, string Email, DateTimeOffset AtUtc, string? Ip);

/// <summary>
/// Emails the owner when a new account is created on a hosted deployment.
/// </summary>
/// <remarks>
/// <para>
/// The owner's own Anthropic key pays for every user's generations (see <see cref="HostedAuth"/>'s
/// note on the shared key), and sign-up is open to the internet with only a per-user daily cap. So
/// "who just signed up" is money-relevant, not a nicety — a notification is how the owner notices a
/// spike, an abuse pattern, or simply their first real trial user, without watching the box.
/// </para>
/// <para>
/// <b>The notice carries four things and only four things:</b> name, email address, the UTC time,
/// and the IP the sign-up arrived from. Not the password, not the password hash, not the session
/// cookie, not any eBay or Anthropic token — none of those are passed in, so none of them can be
/// mailed by mistake. Mail is not a confidential channel and this one crosses Gmail; the rule is
/// that a copy of this message in the wrong mailbox must not be worth anything to whoever has it.
/// </para>
/// <para>
/// Configuration (all under <c>Notify:Smtp</c>; env form <c>Notify__Smtp__*</c>). <b>Unset means
/// disabled, and disabled is a silent no-op</b> — the desktop build, the tests, and any deployment
/// that has not set SMTP up are completely unaffected:
/// <list type="bullet">
///   <item><c>Host</c> — default <c>smtp.gmail.com</c></item>
///   <item><c>Port</c> — default <c>587</c> (STARTTLS)</item>
///   <item><c>User</c> — the SMTP login</item>
///   <item><c>Password</c> — an app password, never the account password</item>
///   <item><c>From</c> — defaults to <c>User</c> (Gmail requires the From to be the authenticated user)</item>
///   <item><c>To</c> — where the notice goes (the owner)</item>
/// </list>
/// </para>
/// <para>
/// Two rules outrank the feature itself. <b>Sending must never cost a sign-up:</b>
/// <see cref="NotifySignupAsync"/> is called fire-and-forget and swallows every failure to the log,
/// so a slow or refusing SMTP server cannot delay the response, and a new user is never told their
/// account was not created because the owner's mailbox was unreachable. And <b>a flood of sign-ups
/// must not become a flood of mail</b> — see <see cref="MaxPerWindow"/>: past the cap the individual
/// notices stop and one summary takes their place, because the account doing the sending is a
/// personal Gmail account and Gmail's answer to a few hundred messages in an hour is to block it,
/// which would cost the owner the notifications that actually mattered.
/// </para>
/// </remarks>
public sealed class SignupNotifier : IDisposable
{
    /// <summary>How many individual notices may be sent in one <see cref="Window"/>.</summary>
    public const int MaxPerWindow = 20;

    /// <summary>The rate-limit window. The count starts again when it rolls over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>
    /// How many suppressed sign-ups the summary lists individually before it stops naming them and
    /// just counts them. A flood is exactly when this list would otherwise be enormous, and an
    /// enormous list is both a mail nobody reads and a slice of memory an attacker chooses the size
    /// of.
    /// </summary>
    private const int MaxListedInSummary = 50;

    private readonly IConfiguration _config;
    private readonly ILogger<SignupNotifier> _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<MailMessage, CancellationToken, Task> _transport;

    private readonly object _gate = new();
    private DateTimeOffset _windowStartedAt = DateTimeOffset.MinValue;
    private int _sentThisWindow;
    private int _suppressedThisWindow;
    private readonly List<SignupNotice> _suppressed = [];

    /// <summary>
    /// Fires once at the end of a window in which something was suppressed, so the summary arrives
    /// even if the flood stops dead and no later sign-up comes along to carry it out.
    /// </summary>
    private System.Threading.Timer? _summaryTimer;
    private bool _disposed;

    public SignupNotifier(IConfiguration config, ILogger<SignupNotifier> log)
        : this(config, log, clock: null, transport: null) { }

    /// <param name="clock">Overridable so a test can watch a window roll over without waiting an hour.</param>
    /// <param name="transport">
    /// Overridable so a test can count what would have been sent without a mail server. Production
    /// leaves it null and gets <see cref="SendOverSmtpAsync"/>.
    /// </param>
    public SignupNotifier(IConfiguration config, ILogger<SignupNotifier> log,
                          Func<DateTimeOffset>? clock,
                          Func<MailMessage, CancellationToken, Task>? transport)
    {
        _config    = config;
        _log       = log;
        _clock     = clock ?? (() => DateTimeOffset.UtcNow);
        _transport = transport ?? SendOverSmtpAsync;
    }

    private string Host => Trim(_config["Notify:Smtp:Host"]) ?? "smtp.gmail.com";
    private int Port => int.TryParse(_config["Notify:Smtp:Port"], out var p) && p > 0 ? p : 587;
    private string? User => Trim(_config["Notify:Smtp:User"]);
    private string? Password => Trim(_config["Notify:Smtp:Password"]);
    private string From => Trim(_config["Notify:Smtp:From"]) ?? User ?? "";
    private string? To => Trim(_config["Notify:Smtp:To"]);

    /// <summary>True when enough is configured to actually send. When false, every call is a no-op.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(User) && !string.IsNullOrWhiteSpace(Password) && !string.IsNullOrWhiteSpace(To);

    /// <summary>
    /// Fire-and-forget: tell the owner a new account was created. Never throws — a failure here is
    /// logged and dropped, because the account already exists and the user is already signed in.
    /// </summary>
    /// <remarks>
    /// Past <see cref="MaxPerWindow"/> in a <see cref="Window"/> this sends nothing and remembers the
    /// sign-up for the summary instead. Nothing about that is visible to the person signing up.
    /// </remarks>
    public async Task NotifySignupAsync(string newUserEmail, string? ip, string? newUserName = null,
                                        CancellationToken ct = default)
    {
        if (!IsConfigured) return;

        var notice = new SignupNotice(newUserName, newUserEmail, _clock().ToUniversalTime(), ip);

        // Both decisions are taken under the lock and acted on outside it: sending holds a socket
        // open for as long as the far end feels like taking, and no other sign-up should wait on it.
        MailMessage? summary;
        bool sendIndividually;
        lock (_gate)
        {
            summary = RollWindowIfDue(notice.AtUtc);

            if (_sentThisWindow < MaxPerWindow)
            {
                _sentThisWindow++;
                sendIndividually = true;
            }
            else
            {
                sendIndividually = false;
                _suppressedThisWindow++;
                if (_suppressed.Count < MaxListedInSummary) _suppressed.Add(notice);
                ArmSummaryTimer();
            }
        }

        if (summary is not null) await SendAsync(summary, "rate-limit summary", ct);

        if (sendIndividually)
            await SendAsync(Individual(notice), notice.Email, ct);
        else
            _log.LogInformation(
                "Sign-up notification rate-limited ({Max} already sent this hour); it will be summarised instead",
                MaxPerWindow);
    }

    /// <summary>
    /// Sends the summary for a window that has ended, if one is owed. Called by the timer, so the
    /// owner hears about a flood at the end of the hour it happened in rather than whenever the next
    /// sign-up happens to arrive — which, if the flood was the last thing that ever happened, is
    /// never. Safe to call at any time; a no-op when nothing is owed.
    /// </summary>
    public async Task FlushDueSummaryAsync(CancellationToken ct = default)
    {
        MailMessage? summary;
        lock (_gate) summary = RollWindowIfDue(_clock().ToUniversalTime());
        if (summary is not null) await SendAsync(summary, "rate-limit summary", ct);
    }

    /// <summary>
    /// How many individual notices are still allowed in the current window. For tests and for the
    /// log; nothing decides anything by it.
    /// </summary>
    public int RemainingThisWindow
    {
        get
        {
            lock (_gate)
                return _clock().ToUniversalTime() - _windowStartedAt >= Window
                    ? MaxPerWindow
                    : Math.Max(0, MaxPerWindow - _sentThisWindow);
        }
    }

    /// <summary>
    /// Starts a new counting window if the current one is over, returning the summary owed for the
    /// window that just closed (or null when nothing was suppressed in it). Caller holds the lock.
    /// </summary>
    private MailMessage? RollWindowIfDue(DateTimeOffset now)
    {
        if (now - _windowStartedAt < Window) return null;

        MailMessage? summary = null;
        if (_suppressedThisWindow > 0)
        {
            summary = Summary(_windowStartedAt, _sentThisWindow, _suppressedThisWindow, [.. _suppressed]);
            // The summary is itself a message: it counts against the new window, so a sign-up
            // arriving right behind it cannot make this a cap of twenty-one.
            _sentThisWindow = 1;
        }
        else
        {
            _sentThisWindow = 0;
        }

        _windowStartedAt      = now;
        _suppressedThisWindow = 0;
        _suppressed.Clear();
        return summary;
    }

    /// <summary>
    /// Wakes up once, at the end of the current window, to post the summary. Caller holds the lock;
    /// re-arming while one is already pending is deliberate — the due time only moves later as the
    /// flood goes on, and a callback that finds nothing owed does nothing.
    /// </summary>
    private void ArmSummaryTimer()
    {
        if (_disposed) return;

        var due = _windowStartedAt + Window - _clock().ToUniversalTime() + TimeSpan.FromSeconds(1);
        if (due < TimeSpan.Zero) due = TimeSpan.Zero;

        // System.Threading.Timer explicitly: this project references WinForms for the desktop build,
        // and an unqualified Timer there is the one that needs a message loop and would never fire.
        _summaryTimer ??= new System.Threading.Timer(
            _ => _ = FlushDueSummaryAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        try { _summaryTimer.Change(due, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* shutting down; the summary is not worth an exception */ }
    }

    /// <summary>The notice for one sign-up. Four facts, nothing else. See the class remarks.</summary>
    private MailMessage Individual(SignupNotice notice)
    {
        var body = new StringBuilder()
            .Append("A new account was created on app.inglisting.com.\n\n")
            .Append($"Name:       {Given(notice.Name)}\n")
            .Append($"Email:      {notice.Email}\n")
            .Append($"When (UTC): {Stamp(notice.AtUtc)}\n")
            .Append($"IP:         {Given(notice.Ip)}\n")
            .ToString();

        var message = new MailMessage(From, To!)
        {
            Subject = string.IsNullOrWhiteSpace(notice.Name)
                ? $"New ING Listing Engine sign-up: {notice.Email}"
                : $"New ING Listing Engine sign-up: {notice.Name} ({notice.Email})",
            Body = body,
            IsBodyHtml = false,
        };

        // Reply goes straight to the person who signed up. It repeats the address that is already
        // in the body and adds nothing new. Guarded: a malformed address must drop the reply-to,
        // not the whole notification.
        try { message.ReplyToList.Add(new MailAddress(notice.Email)); } catch (FormatException) { }
        return message;
    }

    /// <summary>
    /// The one message that stands in for everything the cap held back. Same four facts per
    /// sign-up, listed up to <see cref="MaxListedInSummary"/> and counted past that.
    /// </summary>
    private MailMessage Summary(DateTimeOffset windowStart, int sent, int suppressed, IReadOnlyList<SignupNotice> listed)
    {
        var body = new StringBuilder()
            .Append("Sign-up notifications were rate-limited on app.inglisting.com.\n\n")
            .Append($"In the hour from {Stamp(windowStart)} UTC, {sent} sign-ups were emailed individually ")
            .Append($"and {suppressed} more were not, so that a burst of sign-ups cannot turn into a burst of mail.\n\n")
            .Append(suppressed > listed.Count
                ? $"The first {listed.Count} of the {suppressed}:\n\n"
                : "The ones that were not emailed individually:\n\n");

        foreach (var notice in listed)
            body.Append($"  {Stamp(notice.AtUtc)}  {Given(notice.Name)} <{notice.Email}>  from {Given(notice.Ip)}\n");

        if (suppressed > listed.Count)
            body.Append($"\n… and {suppressed - listed.Count} more, not listed.\n");

        return new MailMessage(From, To!)
        {
            Subject = $"ING Listing Engine: {sent + suppressed} sign-ups in an hour ({suppressed} not emailed individually)",
            Body = body.ToString(),
            IsBodyHtml = false,
        };
    }

    /// <summary>
    /// Sends and swallows. Every caller is on the sign-up path or a timer thread, and neither has
    /// anywhere useful to put an exception.
    /// </summary>
    private async Task SendAsync(MailMessage message, string what, CancellationToken ct)
    {
        try
        {
            using (message)
                await _transport(message, ct);
            _log.LogInformation("Sign-up notification sent to owner ({What})", what);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sign-up notification email failed ({What}) — the sign-up itself was unaffected", what);
        }
    }

    private async Task SendOverSmtpAsync(MailMessage message, CancellationToken ct)
    {
        using var client = new SmtpClient(Host, Port)
        {
            EnableSsl = true, // STARTTLS on 587
            Credentials = new NetworkCredential(User, Password),
        };
        await client.SendMailAsync(message, ct);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _summaryTimer?.Dispose();
            _summaryTimer = null;
        }
    }

    private static string Stamp(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private static string Given(string? value) => string.IsNullOrWhiteSpace(value) ? "not given" : value.Trim();

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
