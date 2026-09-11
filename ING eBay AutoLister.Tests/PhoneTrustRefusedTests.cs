namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// A phone that refused this computer's certificate is reported as exactly that, with the fix.
/// </summary>
/// <remarks>
/// <para>
/// 2026-09-10, owner: "it still does not show the live feed when you are taking a photo on the
/// desktop." Measured on the running 2.6.5: the phone asked for <c>/start</c>, then for the
/// certificate-free <c>/c</c> page, then posted one photo — and never made a single request on the
/// HTTPS port. The served certificate was valid and signed by the authority on offer (checked with
/// openssl against <c>/ing-photo-box-ca.cer</c>). The phone simply does not trust it yet, and the
/// desk answered that with "No camera yet." beside the photograph it had just received.
/// </para>
/// <para>
/// The only place that state is visible is the TCP layer: Safari connects to the secure port,
/// reads the certificate, and hangs up without sending a request. So a connection middleware ahead
/// of the TLS handshake records every device that opened the port, the request middleware records
/// every device that went on to ask for something, and the difference — opened, never asked — is
/// <c>SecureRefused</c>. It is the opposite fix from "never reached the port" (network, firewall),
/// which is why the two are told apart rather than merged into "setup needed".
/// </para>
/// </remarks>
public class PhoneTrustRefusedTests
{
    private static readonly string Source = ReadRepoFile(Path.Combine("ING eBay AutoLister", "Services", "PhoneCapture.cs"));
    private static readonly string Js = ReadRepoFile(Path.Combine("ING eBay AutoLister", "wwwroot", "app.js"));
    private static readonly string Html = ReadRepoFile(Path.Combine("ING eBay AutoLister", "wwwroot", "index.html"));

    [Fact]
    public void The_secure_port_is_watched_below_the_handshake()
    {
        // The observer must be added BEFORE UseHttps on the same listener: after it, a connection
        // the phone refuses never reaches it, and the whole signal is gone.
        var listener = Between(Source, "k.ListenAnyIP(Port, o =>", "TrustPort = PickTrustPort();");
        var observe = listener.IndexOf("_secureOpened[Canonical(ep.Address)]", StringComparison.Ordinal);
        var https = listener.IndexOf("o.UseHttps(Certificate())", StringComparison.Ordinal);
        Assert.True(observe >= 0, "the HTTPS listener no longer records who opened it");
        Assert.True(https > observe, "the connection observer must run before the TLS handshake, not after it");

        // And a request on the secure port is what clears it.
        Assert.Contains("if (ctx.Connection.LocalPort == Port && ctx.Connection.RemoteIpAddress is { } accepted)", Source, StringComparison.Ordinal);
        Assert.Contains("_secureSpoke[Canonical(accepted)] = now;", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void This_computers_own_probes_are_never_read_as_a_phone_refusing()
    {
        // publish/check scripts and the owner's own browser hit 9443 from this machine. Without the
        // exclusion every ship would light the panel up with a phantom untrusting phone.
        var refusal = Between(Source, "private (string Ip, DateTimeOffset At)? SecureRefusal()", "return latest;");
        Assert.Contains("if (IsThisMachine(ip)) continue;", refusal, StringComparison.Ordinal);
        Assert.Contains("IPAddress.IsLoopback(parsed)", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_status_carries_the_refusal_and_the_start_page_can_ask_about_it()
    {
        Assert.Contains("bool SecureRefused = false, string SecureRefusedBy = \"\");", Source, StringComparison.Ordinal);
        Assert.Contains("SecureRefused: refusal is not null,", Source, StringComparison.Ordinal);

        // The phone-side page asks the plain-HTTP port what the secure port saw, and only there:
        // on the HTTPS port the answer would sit behind the very wall it describes.
        Assert.Contains("web.MapGet(\"/secure-check\"", Source, StringComparison.Ordinal);
        Assert.Contains("if (ctx.Connection.LocalPort != TrustPort) return Results.NotFound();", Source, StringComparison.Ordinal);
        var page = Between(Source, "function showSetup() {", "function tryCamera() {");
        Assert.Contains("fetch('/secure-check'", page, StringComparison.Ordinal);
        Assert.Contains("does not trust this computer yet", page, StringComparison.Ordinal);
        Assert.Contains("could not reach the secure camera port", page, StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_desk_flow_does_not_send_the_seller_back_to_certificate_setup()
    {
        var state = Between(Js, "state.textContent = st.phoneConnected", "state.className = 'pb-phone-state");
        Assert.Contains("Take photos on the phone", state, StringComparison.Ordinal);
        Assert.Contains("No certificate is required", state, StringComparison.Ordinal);
        Assert.DoesNotContain("Certificate Trust Settings", state, StringComparison.Ordinal);

        var rail = Between(Js, "const rail = st.phoneSending", "async function pbPhoneRefresh");
        Assert.Contains("iPhone camera ready", rail, StringComparison.Ordinal);
        Assert.DoesNotContain("certificate not trusted", rail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_rail_no_longer_overwrites_what_the_caller_just_said()
    {
        // pbNoCamera used to end with "No camera yet." and a dark dot no matter what the caller had
        // written a line earlier. The owner's screenshot shows the result: "No camera yet." beside
        // "Phone photo saved, enhanced, and added below."
        var fn = Between(Js, "function pbNoCamera(message, rail = 'No camera yet.', live = false)", "function pbShowPhonePreview");
        Assert.Contains("status.textContent = rail;", fn, StringComparison.Ordinal);
        Assert.Contains("classList.toggle('is-live', !!live)", fn, StringComparison.Ordinal);
        Assert.DoesNotContain("status.textContent = 'No camera yet.'", fn, StringComparison.Ordinal);
    }

    [Fact]
    public void The_browser_is_made_to_fetch_the_changed_script()
    {
        AssetStamp.AtLeast(Html, "app.js?v=", 169);
    }

    private static string Between(string text, string from, string to)
    {
        var a = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(a >= 0, $"\"{from}\" is no longer in the source.");
        var b = text.IndexOf(to, a + from.Length, StringComparison.Ordinal);
        Assert.True(b > a, $"\"{to}\" no longer follows \"{from}\".");
        return text[a..b];
    }

    private static string ReadRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative));
    }
}
