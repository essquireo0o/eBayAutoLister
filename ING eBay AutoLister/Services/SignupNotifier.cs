using System.Net;
using System.Net.Mail;

namespace ING_eBay_AutoLister.Services;

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
/// Sending must never affect sign-up. <see cref="NotifySignupAsync"/> is called fire-and-forget and
/// swallows every failure to the log: a sign-up that already succeeded must not report failure
/// because an SMTP server was slow, and a new user must never be told their account was not created
/// because the owner's mailbox was unreachable.
/// </para>
/// </remarks>
public sealed class SignupNotifier(IConfiguration config, ILogger<SignupNotifier> log)
{
    private string Host => Trim(config["Notify:Smtp:Host"]) ?? "smtp.gmail.com";
    private int Port => int.TryParse(config["Notify:Smtp:Port"], out var p) && p > 0 ? p : 587;
    private string? User => Trim(config["Notify:Smtp:User"]);
    private string? Password => Trim(config["Notify:Smtp:Password"]);
    private string From => Trim(config["Notify:Smtp:From"]) ?? User ?? "";
    private string? To => Trim(config["Notify:Smtp:To"]);

    /// <summary>True when enough is configured to actually send. When false, every call is a no-op.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(User) && !string.IsNullOrWhiteSpace(Password) && !string.IsNullOrWhiteSpace(To);

    /// <summary>
    /// Fire-and-forget: tell the owner a new account was created. Never throws — a failure here is
    /// logged and dropped, because the account already exists and the user is already signed in.
    /// </summary>
    public async Task NotifySignupAsync(string newUserEmail, string? ip, string? userAgent, CancellationToken ct = default)
    {
        if (!IsConfigured) return;
        try
        {
            var body =
                "A new account was created on app.inglisting.com.\n\n" +
                $"Email:      {newUserEmail}\n" +
                $"When (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}\n" +
                $"IP:         {(string.IsNullOrWhiteSpace(ip) ? "unknown" : ip)}\n" +
                $"Browser:    {(string.IsNullOrWhiteSpace(userAgent) ? "unknown" : userAgent)}\n\n" +
                "This user's AI generations are billed to the owner's Anthropic key, capped per day.";

            using var message = new MailMessage(From, To!)
            {
                Subject = $"New ING Listing Engine sign-up: {newUserEmail}",
                Body = body,
                IsBodyHtml = false,
            };
            // Reply goes straight to the person who signed up. Guarded: a malformed address must
            // drop the reply-to, not the whole notification.
            try { message.ReplyToList.Add(new MailAddress(newUserEmail)); } catch (FormatException) { }

            using var client = new SmtpClient(Host, Port)
            {
                EnableSsl = true, // STARTTLS on 587
                Credentials = new NetworkCredential(User, Password),
            };
            await client.SendMailAsync(message, ct);
            log.LogInformation("Sign-up notification sent to owner for {Email}", newUserEmail);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Sign-up notification email failed for {Email} (sign-up itself was unaffected)", newUserEmail);
        }
    }

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
