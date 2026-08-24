using System.Security.Cryptography.X509Certificates;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// Safari would not let anyone onto the phone camera page, and there was no way past it.
/// </summary>
/// <remarks>
/// <para>
/// The camera page has to be HTTPS — no browser hands the camera to an insecure page — and its
/// certificate is made by the app on the seller's own machine, so no phone trusts it. Chrome shows
/// a warning with a way through. <b>Safari does not always offer one</b>, and when it does not, the
/// seller is stuck: "install Chrome on your iPhone" is not something to tell a customer.
/// </para>
/// <para>
/// The owner asked whether the installer could carry the certificate. It cannot: an MSI writes to
/// the Windows certificate store, and an iPhone never consults it. Trust lives on the device doing
/// the trusting. So the phone is handed the certificate once, over plain HTTP — because it cannot
/// be handed over the HTTPS connection that the missing trust is blocking — and after that it is
/// silent forever.
/// </para>
/// <para>
/// Once per PHONE, not once per certificate: the server certificate is pinned to this machine's
/// LAN address and gets reissued when that changes, so what the phone trusts is a small local
/// AUTHORITY that signs it. A laptop that moves between a house and a workshop does not send its
/// owner back to Settings.
/// </para>
/// </remarks>
public class PhoneCameraTrustTests
{
    private static readonly string Source = ReadSource(Path.Combine("Services", "PhoneCapture.cs"));

    // ── The authority: what a phone is actually asked to trust ───────────────────────────────
    // Read-only against the authority this machine already has. Nothing here writes one.

    [Fact]
    public void The_authority_is_an_authority_and_is_built_to_outlast_the_chore_of_trusting_it()
    {
        using var ca = PhoneCapture.Authority();

        var basic = ca.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.True(basic.CertificateAuthority);
        Assert.True(basic.Critical);

        var usage = ca.Extensions.OfType<X509KeyUsageExtension>().Single();
        Assert.True(usage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign));

        // Ten years. Apple's 825-day ceiling is a rule about SERVER certificates; making the
        // authority obey it too would send every seller back through Settings every two years.
        Assert.True(ca.NotAfter > DateTime.Now.AddYears(5),
            $"the authority expires {ca.NotAfter:yyyy-MM-dd}, which is soon enough to become a chore");

        Assert.Equal(ca.Subject, ca.Issuer);   // self-signed, as a root is
        Assert.Contains("ING Photo Box camera authority", ca.Subject, StringComparison.Ordinal);
    }

    // ── The profile: the thing an iPhone actually installs ───────────────────────────────────

    [Fact]
    public void The_profile_is_a_plist_that_installs_the_authority_as_a_root()
    {
        var xml = System.Text.Encoding.UTF8.GetString(PhoneCapture.MobileConfig());

        Assert.StartsWith("<?xml", xml, StringComparison.Ordinal);
        Assert.Contains("<!DOCTYPE plist", xml, StringComparison.Ordinal);
        // The payload type is the whole point: this is what puts it in the certificate store
        // rather than leaving a file in Downloads.
        Assert.Contains("<key>PayloadType</key><string>com.apple.security.root</string>", xml, StringComparison.Ordinal);
        Assert.Contains("<key>PayloadType</key><string>Configuration</string>", xml, StringComparison.Ordinal);
        Assert.Contains("ING Mining LLC", xml, StringComparison.Ordinal);
        // Removable. A profile the seller cannot take off their own phone would be worse than the
        // warning it replaces.
        Assert.Contains("<key>PayloadRemovalDisallowed</key><false/>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_profile_carries_this_machines_own_authority_and_nothing_else()
    {
        var xml = System.Text.Encoding.UTF8.GetString(PhoneCapture.MobileConfig());
        using var ca = PhoneCapture.Authority();

        var expected = Convert.ToBase64String(ca.Export(X509ContentType.Cert));
        Assert.Contains(expected, xml, StringComparison.Ordinal);

        // One payload. A profile that quietly carried a second one would be exactly the thing a
        // person is right to be suspicious of.
        Assert.Equal(1, CountOf(xml, "<key>PayloadCertificateFileName</key>"));
    }

    [Fact]
    public void Installing_it_twice_replaces_it_rather_than_stacking_copies()
    {
        // The identifiers are derived from the authority's thumbprint, so the same machine always
        // produces the same profile identity and iOS treats a re-install as an update.
        var first  = System.Text.Encoding.UTF8.GetString(PhoneCapture.MobileConfig());
        var second = System.Text.Encoding.UTF8.GetString(PhoneCapture.MobileConfig());
        Assert.Equal(first, second);

        Assert.Contains("<key>PayloadIdentifier</key><string>com.ingmining.photobox</string>",
                        first, StringComparison.Ordinal);
    }

    // ── The rules Safari actually enforces on the server certificate ─────────────────────────
    // Miss one of these and Safari does not offer "visit this website anyway" — it just refuses,
    // which is the dead end this whole change exists to remove.

    [Fact]
    public void The_server_certificate_is_issued_by_the_authority_the_phone_trusts()
    {
        // Not self-signed any more. A leaf left over from before this existed would fail against a
        // phone that had dutifully installed the profile, for a reason nobody could work out — so
        // a cached one is re-issued when its issuer is not the current authority.
        Assert.Contains("using var issued = req.Create(ca, notBefore, notAfter, serial);",
                        Source, StringComparison.Ordinal);
        Assert.Contains("string.Equals(existing.Issuer, ca.Subject, StringComparison.Ordinal)",
                        Source, StringComparison.Ordinal);
    }

    [Fact]
    public void It_stays_inside_Apples_validity_ceiling()
    {
        // 825 days is the limit; 397 is what the public CAs settled on, so nothing downstream has
        // to think about it.
        Assert.Contains("notAfter  = notBefore.AddDays(397);", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void It_names_every_address_this_machine_answers_on()
    {
        // iOS ignores the common name entirely; a certificate for last week's IP is a name
        // mismatch, and a name mismatch is one of the errors with no way past.
        Assert.Contains("foreach (var address in wanted) san.AddIpAddress(address);", Source, StringComparison.Ordinal);
        Assert.Contains("private static List<IPAddress> LocalAddresses()", Source, StringComparison.Ordinal);
        Assert.Contains("&& CoversAll(existing, wanted)", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void It_carries_the_server_authentication_purpose()
    {
        Assert.Contains("new X509EnhancedKeyUsageExtension([new Oid(\"1.3.6.1.5.5.7.3.1\")], false)",
                        Source, StringComparison.Ordinal);
    }

    // ── Getting the trust to the phone at all ────────────────────────────────────────────────

    [Fact]
    public void The_trust_page_is_plain_http_because_the_https_page_is_what_is_blocked()
    {
        Assert.Contains("k.ListenAnyIP(TrustPort);", Source, StringComparison.Ordinal);
        Assert.Contains("web.MapGet(\"/trust\"", Source, StringComparison.Ordinal);
        Assert.Contains("application/x-apple-aspen-config", Source, StringComparison.Ordinal);
        // And a plain .cer for everything that is not an iPhone.
        Assert.Contains("ing-photo-box-ca.cer", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_primary_qr_is_the_certificate_free_camera_and_keeps_live_setup_optional()
    {
        Assert.Contains("var url = LaunchUrl;", Source, StringComparison.Ordinal);
        Assert.Contains("$\"http://{LocalAddress()}:{TrustPort}/c/{_token}\"", Source, StringComparison.Ordinal);
        Assert.Contains("web.MapGet(\"/start\"", Source, StringComparison.Ordinal);
        Assert.Contains("web.MapGet(\"/c/{token}\"", Source, StringComparison.Ordinal);
        // The old certificate bootstrap never put a pairing secret in a discoverable /start path.
        Assert.DoesNotContain("/start/{token}", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Trusted_safari_opens_automatically_and_untrusted_safari_gets_the_installer()
    {
        Assert.Contains("web.MapGet(\"/ready.gif\"", Source, StringComparison.Ordinal);
        Assert.Contains("image.src = READY + '?t=' + Date.now();", Source, StringComparison.Ordinal);
        Assert.Contains("location.replace(CAMERA);", Source, StringComparison.Ordinal);
        Assert.Contains("showSetup();", Source, StringComparison.Ordinal);
        Assert.Contains("visibilitychange", Source, StringComparison.Ordinal);
        Assert.Contains("Check again and open camera", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_https_root_finishes_the_pairing_route_instead_of_opening_a_dead_page()
    {
        Assert.Contains("ctx.Connection.LocalPort == Port", Source, StringComparison.Ordinal);
        Assert.Contains("Results.Redirect(PublicUrl)", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_port_is_proved_free_rather_than_assumed()
    {
        // Found the hard way on the machine this was written on: an antivirus service already held
        // 0.0.0.0:9444, and ListenAnyIP does not fail for that — it binds the IPv6 half, reports
        // itself started, and every IPv4 request goes to the other process, which accepts the
        // connection and never answers. Indistinguishable from the app being broken, and silent.
        Assert.Contains("private static int PickTrustPort()", Source, StringComparison.Ordinal);
        Assert.Contains("probe.Server.ExclusiveAddressUse = true;", Source, StringComparison.Ordinal);
        Assert.Contains("TrustPort = PickTrustPort();", Source, StringComparison.Ordinal);
        // So the address the QR points at is the port that actually answered.
        Assert.Contains("public static int TrustPort { get; private set; }", Source, StringComparison.Ordinal);
        Assert.Contains("$\"http://{LocalAddress()}:{TrustPort}/trust\"", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_screen_stops_telling_iPhone_users_to_tap_through_the_warning()
    {
        var html = ReadAsset("index.html");

        // Advice that only works in Chrome, given to somebody holding an iPhone.
        Assert.DoesNotContain("Tap <b>Show details</b>, then", html, StringComparison.Ordinal);
        Assert.Contains("optional live-studio setup", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no certificate, profile or browser permission is needed", html,
                        StringComparison.OrdinalIgnoreCase);
        Assert.Contains("id=\"pb-trust-qr\"", html, StringComparison.Ordinal);
        // The step no installer is allowed to do for the seller, said plainly rather than left to
        // be discovered.
        // Asserted on the words rather than on where the line happens to wrap.
        Assert.Contains("Certificate Trust", html, StringComparison.Ordinal);
        Assert.Contains("ING Photo Box camera authority", html, StringComparison.Ordinal);
    }

    private static int CountOf(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }

    private static string ReadSource(string relative) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", relative));

    private static string ReadAsset(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", "wwwroot", name));
}
